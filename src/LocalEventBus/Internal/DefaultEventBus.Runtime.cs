using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

public sealed partial class DefaultEventBus
{
    #region IEventPublisher Implementation

    public ValueTask PublishAsync<TEvent>(
        TEvent @event,
        string? topic = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(@event);
        EnsureDispatchLoopStarted();

        topic = ResolvePublishedTopic(typeof(TEvent), topic);
        return PublishToMatchedChannelsAsync(
            eventData: @event,
            eventType: typeof(TEvent),
            topic: topic,
            cancellationToken: cancellationToken);
    }

    public void Publish<TEvent>(TEvent @event, string? topic = null)
        where TEvent : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(@event);
        EnsureDispatchLoopStarted();

        topic = ResolvePublishedTopic(typeof(TEvent), topic);
        PublishToMatchedChannelsSync(
            eventData: @event,
            eventType: typeof(TEvent),
            topic: topic);
    }

    /// <summary>
    /// 发布纯 Topic 事件
    /// </summary>
    public ValueTask PublishByTopicAsync(
        string topic,
        object? eventData = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        EnsureDispatchLoopStarted();

        return PublishToMatchedChannelsAsync(
            eventData: eventData,
            eventType: eventData?.GetType() ?? typeof(object),
            topic: topic,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 同步发布纯 Topic 事件（无强类型）
    /// </summary>
    public void PublishByTopic(
        string topic,
        object? eventData = null)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        EnsureDispatchLoopStarted();

        PublishToMatchedChannelsSync(
            eventData: eventData,
            eventType: eventData?.GetType() ?? typeof(object),
            topic: topic);
    }

    #endregion

    #region Direct Invocation

    /// <summary>
    /// 直接调用订阅者(不经过事件队列)
    /// </summary>
    public ValueTask InvokeAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(@event);
        MarkDispatchStarted();

        // 获取 Topic
        var topic = _keyProvider.GetKey(typeof(TEvent));
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        if (subscribers.Length != 1)
        {
            throw new InvalidOperationException(
                $"匹配订阅者不唯一。Topic: '{topic}'，匹配数量: {subscribers.Length}。");
        }

        return InvokeSubscriberDirectlyAsync(@event, subscribers[0], cancellationToken);
    }

    /// <summary>
    /// 通过 Topic 直接调用订阅者
    /// </summary>
    public ValueTask InvokeByTopicAsync(string topic, object? eventData = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(topic);
        MarkDispatchStarted();

        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        if (subscribers.Length != 1)
        {
            throw new InvalidOperationException(
                $"匹配订阅者不唯一。Topic: '{topic}'，匹配数量: {subscribers.Length}。");
        }

        return InvokeSubscriberDirectlyAsync(eventData, subscribers[0], cancellationToken);
    }

    /// <summary>
    /// 直接调用单个订阅者(不触发拦截器，仅应用过滤器和超时)
    /// </summary>
    private async ValueTask InvokeSubscriberDirectlyAsync(
        object? eventData,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
    {
        // 1. 过滤器检查（仅当有事件数据时）
        if (eventData != null)
        {
            foreach (var filter in _filters)
            {
                if (!await filter.ShouldProcessAsync(eventData, cancellationToken))
                {
                    return;
                }
            }
        }

        // 2. 应用超时并执行订阅者（直接调用不触发拦截器）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeout = subscriber.Timeout ?? _options.DefaultTimeout;
        cts.CancelAfter(timeout);

        await InvokeSubscriberByThreadOptionAsync(eventData, subscriber, cts.Token);
    }

    #endregion

    #region IEventBusDiagnostics Implementation

    public int GetSubscriberCount() => _subscriberRegistry.Count;

    public int GetPendingEventCount() => _channelManager.GetPendingCount();

    public IReadOnlyList<SubscriberInfo> GetSubscribers() => _subscriberRegistry.GetAllSubscribers();

    public IReadOnlyList<SubscriberInfo> GetSubscribers<TEvent>() where TEvent : notnull
    {
        var topic = _keyProvider.GetKey(typeof(TEvent));
        return _subscriberRegistry.GetSubscribersByPublishedTopic(topic).ToArray();
    }

    #endregion

    #region Event Dispatch Loop

    /// <summary>
    /// 分片事件分发循环（每个分片独立处理，保证顺序性）
    /// </summary>
    private async Task DispatchShardAsync(Channel<EventEnvelope> channel)
    {
        try
        {
            await foreach (var envelope in channel.Reader.ReadAllAsync(_cts.Token))
            {
                try
                {
                    await DispatchEventAsync(envelope);
                }
                catch (Exception)
                {
                    // 记录但不中断循环
                }
                finally
                {
                    _channelManager.DecrementLoad(envelope.ChannelId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
    }

    /// <summary>
    /// 分发单个事件
    /// </summary>
    private async Task DispatchEventAsync(EventEnvelope envelope)
    {
        // 通过 Topic 获取订阅者(支持模式匹配)
        var allMatchedSubscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(envelope.Topic!);
        if (allMatchedSubscribers.IsEmpty)
        {
            return;
        }

        // 路由受众过滤，避免跨通道重复分发
        Task? firstTask = null;
        List<Task>? remainingTasks = null;

        foreach (var subscriber in allMatchedSubscribers)
        {
            if (!MatchesRouteAudience(subscriber, envelope))
            {
                continue;
            }

            var task = ExecuteSubscriberAsync(envelope, subscriber, _cts.Token);
            if (firstTask is null)
            {
                firstTask = task;
                continue;
            }

            remainingTasks ??= new List<Task>(Math.Min(4, allMatchedSubscribers.Length - 1));
            remainingTasks.Add(task);
        }

        if (firstTask is null)
        {
            return;
        }

        if (remainingTasks is null)
        {
            await firstTask;
            return;
        }

        remainingTasks.Add(firstTask);
        await Task.WhenAll(remainingTasks);
    }

    /// <summary>
    /// 执行单个订阅者(带重试和拦截)
    /// </summary>
    private async Task ExecuteSubscriberAsync(
        EventEnvelope envelope,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.EventData;

        // 1. 过滤器检查
        foreach (var filter in _filters)
        {
            if (!await filter.ShouldProcessAsync(eventData!, cancellationToken))
            {
                return;
            }
        }

        // 2. 前置拦截
        foreach (var interceptor in _interceptors)
        {
            await interceptor.OnHandlingAsync(eventData!, subscriber, cancellationToken);
        }

        var sw = Stopwatch.StartNew();
        Exception? lastException = null;
        int attemptNumber = 0;
        var retryOptions = _options.RetryOptions;

        // 3. 执行订阅者（带重试）
        while (attemptNumber <= retryOptions.MaxRetryAttempts)
        {
            attemptNumber++;

            try
            {
                // 应用超时
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timeout = subscriber.Timeout ?? _options.DefaultTimeout;
                cts.CancelAfter(timeout);

                await InvokeSubscriberByThreadOptionAsync(eventData, subscriber, cts.Token);

                // 成功处理
                sw.Stop();

                // 后置拦截
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.OnHandledAsync(eventData!, subscriber, sw.Elapsed, cancellationToken);
                }
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;

                // 检查是否应该重试
                if (attemptNumber > retryOptions.MaxRetryAttempts ||
                    (retryOptions.ShouldRetry != null && !retryOptions.ShouldRetry(ex)))
                {
                    break;
                }

                // 计算重试延迟
                var delay = CalculateRetryDelay(attemptNumber, retryOptions);
                await Task.Delay(delay, cancellationToken);
            }
        }

        // 4. 所有重试失败
        sw.Stop();

        // 失败拦截
        if (lastException != null)
        {
            foreach (var interceptor in _interceptors)
            {
                await interceptor.OnHandlerFailedAsync(eventData!, subscriber, lastException, cancellationToken);
            }
        }
    }

    private TimeSpan CalculateRetryDelay(int attemptNumber, RetryOptions options)
    {
        var delay = options.DelayStrategy switch
        {
            RetryDelayStrategy.Fixed => options.InitialDelay,
            RetryDelayStrategy.Linear => TimeSpan.FromMilliseconds(options.InitialDelay.TotalMilliseconds * attemptNumber),
            RetryDelayStrategy.ExponentialBackoff => TimeSpan.FromMilliseconds(options.InitialDelay.TotalMilliseconds * Math.Pow(2, attemptNumber - 1)),
            _ => options.InitialDelay
        };

        return delay > options.MaxDelay ? options.MaxDelay : delay;
    }

    private static bool MatchesRouteAudience(SubscriberInfo subscriber, EventEnvelope envelope)
    {
        return envelope.RouteAudience switch
        {
            EventRouteAudience.Explicit => subscriber.ChannelId.HasValue &&
                                           subscriber.ChannelId.Value == envelope.ChannelId,
            EventRouteAudience.Unspecified => !subscriber.ChannelId.HasValue,
            _ => false
        };
    }

    #endregion

    #region Runtime Helpers

    private string ResolvePublishedTopic(Type eventType, string? topic)
    {
        if (!string.IsNullOrEmpty(topic))
        {
            return topic;
        }

        return _keyProvider.GetKey(eventType);
    }

    private async ValueTask PublishToMatchedChannelsAsync(
        object? eventData,
        Type eventType,
        string topic,
        CancellationToken cancellationToken)
    {
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);
        if (subscribers.IsEmpty)
        {
            return;
        }

        var channelCount = _channelManager.ChannelCount;
        var explicitChannelFlags = ArrayPool<bool>.Shared.Rent(channelCount);

        try
        {
            Array.Clear(explicitChannelFlags, 0, channelCount);

            var (hasExplicitSubscribers, hasUnspecifiedSubscribers) =
                CollectRouteTargets(subscribers, explicitChannelFlags);

            if (hasExplicitSubscribers)
            {
                for (int explicitChannelId = 1; explicitChannelId < channelCount; explicitChannelId++)
                {
                    if (!explicitChannelFlags[explicitChannelId])
                    {
                        continue;
                    }

                    var explicitEnvelope = CreateEventEnvelope(
                        eventData: eventData,
                        eventType: eventType,
                        topic: topic,
                        channelId: explicitChannelId,
                        routeAudience: EventRouteAudience.Explicit);

                    await EnqueueAsync(explicitEnvelope, cancellationToken);
                }
            }

            if (hasUnspecifiedSubscribers)
            {
                var unspecifiedChannelId = ResolveUnspecifiedRouteChannelId();
                var unspecifiedEnvelope = CreateEventEnvelope(
                    eventData: eventData,
                    eventType: eventType,
                    topic: topic,
                    channelId: unspecifiedChannelId,
                    routeAudience: EventRouteAudience.Unspecified);

                await EnqueueAsync(unspecifiedEnvelope, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(explicitChannelFlags);
        }
    }

    private void PublishToMatchedChannelsSync(
        object? eventData,
        Type eventType,
        string topic)
    {
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);
        if (subscribers.IsEmpty)
        {
            return;
        }

        var channelCount = _channelManager.ChannelCount;
        var explicitChannelFlags = ArrayPool<bool>.Shared.Rent(channelCount);

        try
        {
            Array.Clear(explicitChannelFlags, 0, channelCount);

            var (hasExplicitSubscribers, hasUnspecifiedSubscribers) =
                CollectRouteTargets(subscribers, explicitChannelFlags);

            if (hasExplicitSubscribers)
            {
                for (int explicitChannelId = 1; explicitChannelId < channelCount; explicitChannelId++)
                {
                    if (!explicitChannelFlags[explicitChannelId])
                    {
                        continue;
                    }

                    var explicitEnvelope = CreateEventEnvelope(
                        eventData: eventData,
                        eventType: eventType,
                        topic: topic,
                        channelId: explicitChannelId,
                        routeAudience: EventRouteAudience.Explicit);

                    EnqueueOrThrow(explicitEnvelope);
                }
            }

            if (hasUnspecifiedSubscribers)
            {
                var unspecifiedChannelId = ResolveUnspecifiedRouteChannelId();
                var unspecifiedEnvelope = CreateEventEnvelope(
                    eventData: eventData,
                    eventType: eventType,
                    topic: topic,
                    channelId: unspecifiedChannelId,
                    routeAudience: EventRouteAudience.Unspecified);

                EnqueueOrThrow(unspecifiedEnvelope);
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(explicitChannelFlags);
        }
    }

    private static (bool HasExplicitSubscribers, bool HasUnspecifiedSubscribers) CollectRouteTargets(
        IReadOnlyList<SubscriberInfo> subscribers,
        bool[] explicitChannelFlags)
    {
        bool hasExplicitSubscribers = false;
        bool hasUnspecifiedSubscribers = false;

        for (int i = 0; i < subscribers.Count; i++)
        {
            var subscriber = subscribers[i];
            if (subscriber.ChannelId.HasValue)
            {
                hasExplicitSubscribers = true;
                explicitChannelFlags[subscriber.ChannelId.Value] = true;
            }
            else
            {
                hasUnspecifiedSubscribers = true;
            }
        }

        return (hasExplicitSubscribers, hasUnspecifiedSubscribers);
    }

    private int ResolveUnspecifiedRouteChannelId()
    {
        return _channelManager.SelectLeastLoadedUnspecifiedChannelId();
    }

    private async ValueTask EnqueueAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var channel = _channelManager.GetChannel(envelope.ChannelId);
        _channelManager.IncrementLoad(envelope.ChannelId);

        try
        {
            await channel.Writer.WriteAsync(envelope, cancellationToken);
        }
        catch
        {
            _channelManager.DecrementLoad(envelope.ChannelId);
            throw;
        }
    }

    private bool TryEnqueue(EventEnvelope envelope)
    {
        var channel = _channelManager.GetChannel(envelope.ChannelId);
        _channelManager.IncrementLoad(envelope.ChannelId);

        if (channel.Writer.TryWrite(envelope))
        {
            return true;
        }

        _channelManager.DecrementLoad(envelope.ChannelId);
        return false;
    }

    private void EnqueueOrThrow(EventEnvelope envelope)
    {
        if (TryEnqueue(envelope))
        {
            return;
        }

        throw new InvalidOperationException(
            $"无法将事件写入通道 {envelope.ChannelId}。通道可能已满或不可写。");
    }

    private EventEnvelope CreateEventEnvelope(
        object? eventData,
        Type eventType,
        string topic,
        int channelId,
        EventRouteAudience routeAudience)
    {
        return new EventEnvelope
        {
            EventData = eventData,
            EventType = eventType,
            Timestamp = DateTimeOffset.UtcNow,
            ChannelId = channelId,
            RouteAudience = routeAudience,
            Topic = topic
        };
    }

    private ValueTask InvokeSubscriberByThreadOptionAsync(
        object? eventData,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
    {
        return subscriber.ThreadOption switch
        {
            ThreadOption.BackgroundThread => subscriber.InvokeAsync(eventData, cancellationToken),
            ThreadOption.UIThread => new ValueTask(
                InvokeSubscriberOnUiThreadAsync(eventData, subscriber, cancellationToken)),
            _ => subscriber.InvokeAsync(eventData, cancellationToken)
        };
    }

    private Task InvokeSubscriberOnUiThreadAsync(
        object? eventData,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
    {
        var synchronizationContext = _uiSynchronizationContext;
        if (synchronizationContext is null)
        {
            throw new InvalidOperationException(
                "ThreadOption.UIThread 需要可用的 SynchronizationContext。请在 UI/Main 线程创建 EventBus，或通过构造函数传入 synchronizationContext。");
        }

        if (ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            return subscriber.InvokeAsync(eventData, cancellationToken).AsTask();
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        if (cancellationToken.CanBeCanceled)
        {
            registration = cancellationToken.Register(static state =>
            {
                var source = (TaskCompletionSource<object?>)state!;
                source.TrySetCanceled();
            }, tcs);
        }

        synchronizationContext.Post(async _ =>
        {
            try
            {
                await subscriber.InvokeAsync(eventData, cancellationToken);
                tcs.TrySetResult(null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                registration.Dispose();
            }
        }, null);

        return tcs.Task;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            _channelManager.Dispose();
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            await _channelManager.DisposeAsync();

            var dispatchTasks = Volatile.Read(ref _dispatchTasks);
            if (dispatchTasks is { Length: > 0 })
            {
                try
                {
                    await Task.WhenAll(dispatchTasks);
                }
                catch (OperationCanceledException)
                {
                    // 取消期间属于预期行为
                }
            }

            _cts.Dispose();
        }
    }

    #endregion
}
