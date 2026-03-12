using LocalEventBus.Abstractions;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;

namespace LocalEventBus.Internal;

/// <summary>
/// 默认事件总线实现
/// 支持基于 Topic 的事件路由系统
/// </summary>
public sealed class DefaultEventBus : IEventBus, IEventBusDiagnostics
{
    private readonly EventSubscriberRegistry _subscriberRegistry;
    private readonly EventChannelManager _channelManager;
    private readonly EventBusOptions _options;
    private readonly IEventTypeKeyProvider _keyProvider;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly List<IEventFilter> _filters = [];
    private readonly List<IEventInterceptor> _interceptors = [];
    private readonly CancellationTokenSource _cts = new();
    private int _dispatchStarted;
    private readonly int[] _explicitChannelSubscriberCounts;
    private Task[]? _dispatchTasks;
    private int _disposed;

    /// <summary>
    /// 创建默认事件总线实例
    /// </summary>
    /// <param name="options">配置选项</param>
    /// <param name="keyProvider">类型键提取器</param>
    /// <param name="matcherProvider">匹配器提供者</param>
    /// <param name="filters">过滤器集合</param>
    /// <param name="interceptors">拦截器集合</param>
    /// <param name="synchronizationContext">UI/Main 线程上下文（用于 ThreadOption.UIThread）</param>
    public DefaultEventBus(
        EventBusOptions options,
        IEventTypeKeyProvider keyProvider,
        IEventMatcherProvider matcherProvider,
        IEnumerable<IEventFilter>? filters = null,
        IEnumerable<IEventInterceptor>? interceptors = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _uiSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current;

        // 使用注入的匹配器提供者创建订阅者注册表
        _subscriberRegistry = new EventSubscriberRegistry(matcherProvider);
        _channelManager = new EventChannelManager(_options);
        _explicitChannelSubscriberCounts = new int[_channelManager.ChannelCount];

        // 添加过滤器和拦截器
        if (filters != null)
        {
            _filters.AddRange(filters);
            _filters.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        if (interceptors != null)
        {
            _interceptors.AddRange(interceptors);
            _interceptors.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    /// <summary>
    /// 创建默认事件总线实例
    /// </summary>
    public DefaultEventBus(
        EventBusOptions? options = null,
        SynchronizationContext? synchronizationContext = null)
        : this(options ?? new EventBusOptions(),
            new DefaultEventTypeKeyProvider(),
            new DefaultEventMatcherProvider(),
            null,
            null,
            synchronizationContext)
    {
    }

    /// <summary>
    /// 启动事件分发循环
    /// </summary>
    private void EnsureDispatchLoopStarted()
    {
        if (_dispatchTasks is null)
        {
            lock (this)
            {
                if (_dispatchTasks is null)
                {
                    Interlocked.Exchange(ref _dispatchStarted, 1);
                    // 为每个分片创建独立的处理任务
                    var shardedChannels = _channelManager.GetShardedChannels();
                    _dispatchTasks = new Task[shardedChannels.Count];

                    for (int i = 0; i < shardedChannels.Count; i++)
                    {
                        var shardIndex = i;
                        _dispatchTasks[i] = Task.Run(() => DispatchShardAsync(shardedChannels[shardIndex]), _cts.Token);
                    }
                }
            }
        }
    }

    #region IEventPublisher Implementation

    public async ValueTask PublishAsync<TEvent>(
        TEvent @event,
        string? topic = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);
        EnsureDispatchLoopStarted();

        topic = ResolvePublishedTopic(typeof(TEvent), topic);
        await PublishToMatchedChannelsAsync(
            eventData: @event,
            eventType: typeof(TEvent),
            topic: topic,
            cancellationToken: cancellationToken);
    }

    public void Publish<TEvent>(TEvent @event, string? topic = null)
        where TEvent : notnull
    {
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
    public async ValueTask PublishByTopicAsync(
        string topic,
        object? eventData = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        EnsureDispatchLoopStarted();

        await PublishToMatchedChannelsAsync(
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
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        EnsureDispatchLoopStarted();

        PublishToMatchedChannelsSync(
            eventData: eventData,
            eventType: eventData?.GetType() ?? typeof(object),
            topic: topic);
    }

    #endregion

    #region IEventSubscriber Implementation

    public IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscriberInfo = CreateSubscriberInfoFromDelegate(handler, options);
        _subscriberRegistry.AddSubscriber(subscriberInfo);
        TrackExplicitChannelSubscriber(subscriberInfo);

        return new SubscriptionToken(() =>
        {
            _subscriberRegistry.RemoveSubscriber(subscriberInfo);
            UntrackExplicitChannelSubscriber(subscriberInfo);
        });
    }

    public IDisposable Subscribe<TEvent>(
        Action<TEvent> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        // 包装为异步委托
        Func<TEvent, CancellationToken, ValueTask> asyncHandler = (e, _) =>
        {
            handler(e);
            return ValueTask.CompletedTask;
        };

        return Subscribe(asyncHandler, options);
    }

    public IDisposable Subscribe(object subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        var subscriberInfos = ScanSubscriberMethods(subscriber);

        foreach (var info in subscriberInfos)
        {
            _subscriberRegistry.AddSubscriber(info);
            TrackExplicitChannelSubscriber(info);
        }

        return new SubscriptionToken(() =>
        {
            foreach (var info in subscriberInfos)
            {
                _subscriberRegistry.RemoveSubscriber(info);
                UntrackExplicitChannelSubscriber(info);
            }
        });
    }

    /// <summary>
    /// 订阅纯 Topic
    /// </summary>
    public IDisposable Subscribe(
        string topic,
        Func<object?, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);

        var subscriberInfo = CreateTopicSubscriberInfo(topic, handler, options);
        _subscriberRegistry.AddSubscriber(subscriberInfo);
        TrackExplicitChannelSubscriber(subscriberInfo);

        return new SubscriptionToken(() =>
        {
            _subscriberRegistry.RemoveSubscriber(subscriberInfo);
            UntrackExplicitChannelSubscriber(subscriberInfo);
        });
    }

    /// <summary>
    /// 订阅纯 Topic
    /// </summary>
    public IDisposable Subscribe(
        string topic,
        Action<object?> handler,
        SubscribeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(handler);

        // 包装为异步委托
        Func<object?, CancellationToken, ValueTask> asyncHandler = (e, _) =>
        {
            handler(e);
            return ValueTask.CompletedTask;
        };

        return Subscribe(topic, asyncHandler, options);
    }


    /// <inheritdoc/>
    public void Unsubscribe(object subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var subscribersToRemove = _subscriberRegistry.GetSubscribersByTarget(subscriber);

        _subscriberRegistry.RemoveSubscribersByTarget(subscriber);

        foreach (var item in subscribersToRemove)
        {
            UntrackExplicitChannelSubscriber(item);
        }
    }

    #endregion

    #region Direct Invocation

    /// <summary>
    /// 直接调用订阅者（不经过事件队列）
    /// </summary>
    public async ValueTask InvokeAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(@event);
        Interlocked.Exchange(ref _dispatchStarted, 1);

        // 获取 Topic
        var topic = _keyProvider.GetKey(typeof(TEvent));
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        if (subscribers.IsEmpty)
        {
            return;
        }

        if (subscribers.Length > 1)
        {
            throw new InvalidOperationException(
                $"InvokeAsync 要求最多一个匹配订阅者。Topic: '{topic}'，匹配数量: {subscribers.Length}。");
        }

        await InvokeSubscriberDirectlyAsync(@event, subscribers[0], cancellationToken);
    }

    /// <summary>
    /// 通过 Topic 直接调用订阅者
    /// </summary>
    public async ValueTask InvokeByTopicAsync(string topic, object? eventData = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        Interlocked.Exchange(ref _dispatchStarted, 1);

        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        if (subscribers.IsEmpty)
        {
            return;
        }

        if (subscribers.Length > 1)
        {
            throw new InvalidOperationException(
                $"InvokeByTopicAsync 要求最多一个匹配订阅者。Topic: '{topic}'，匹配数量: {subscribers.Length}。");
        }

        await InvokeSubscriberDirectlyAsync(eventData, subscribers[0], cancellationToken);
    }

    /// <summary>
    /// 直接调用单个订阅者（不触发拦截器，仅应用过滤器和超时）
    /// </summary>
    private async Task InvokeSubscriberDirectlyAsync(
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
        // 通过 Topic 获取订阅者（支持模式匹配）
        var allMatchedSubscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(envelope.Topic!);
        if (allMatchedSubscribers.IsEmpty)
        {
            return;
        }

        // 路由受众过滤，避免跨通道重复分发
        var subscribers = envelope.RouteAudience switch
        {
            EventRouteAudience.Explicit => allMatchedSubscribers
                .Where(s => s.ChannelId.HasValue && s.ChannelId.Value == envelope.ChannelId)
                .ToArray(),
            EventRouteAudience.Unspecified => allMatchedSubscribers
                .Where(s => !s.ChannelId.HasValue)
                .ToArray(),
            _ => Array.Empty<SubscriberInfo>()
        };

        if (subscribers.Length == 0)
        {
            return;
        }

        var tasks = subscribers.Select(subscriber =>
            ExecuteSubscriberAsync(envelope, subscriber, _cts.Token));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 执行单个订阅者（带重试和拦截）
    /// </summary>
    private async Task ExecuteSubscriberAsync(
        EventEnvelope envelope,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
    {
        // 1. 过滤器检查
        foreach (var filter in _filters)
        {
            if (!await filter.ShouldProcessAsync(envelope.EventData, cancellationToken))
            {
                return;
            }
        }

        // 2. 前置拦截
        foreach (var interceptor in _interceptors)
        {
            await interceptor.OnHandlingAsync(envelope.EventData, subscriber, cancellationToken);
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

                await InvokeSubscriberByThreadOptionAsync(envelope.EventData, subscriber, cts.Token);

                // 成功处理
                sw.Stop();

                // 后置拦截
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.OnHandledAsync(envelope.EventData, subscriber, sw.Elapsed, cancellationToken);
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
                await interceptor.OnHandlerFailedAsync(envelope.EventData, subscriber, lastException, cancellationToken);
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

    #endregion

    #region Helper Methods

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

        var explicitChannelIds = new HashSet<int>();
        bool hasUnspecifiedSubscribers = false;

        foreach (var subscriber in subscribers)
        {
            if (subscriber.ChannelId.HasValue)
            {
                explicitChannelIds.Add(subscriber.ChannelId.Value);
            }
            else
            {
                hasUnspecifiedSubscribers = true;
            }
        }

        foreach (var explicitChannelId in explicitChannelIds)
        {
            var explicitEnvelope = CreateEventEnvelope(
                eventData: eventData,
                eventType: eventType,
                topic: topic,
                channelId: explicitChannelId,
                routeAudience: EventRouteAudience.Explicit);

            await EnqueueAsync(explicitEnvelope, cancellationToken);
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

        var explicitChannelIds = new HashSet<int>();
        bool hasUnspecifiedSubscribers = false;

        foreach (var subscriber in subscribers)
        {
            if (subscriber.ChannelId.HasValue)
            {
                explicitChannelIds.Add(subscriber.ChannelId.Value);
            }
            else
            {
                hasUnspecifiedSubscribers = true;
            }
        }

        foreach (var explicitChannelId in explicitChannelIds)
        {
            var explicitEnvelope = CreateEventEnvelope(
                eventData: eventData,
                eventType: eventType,
                topic: topic,
                channelId: explicitChannelId,
                routeAudience: EventRouteAudience.Explicit);

            EnqueueOrThrow(explicitEnvelope);
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

    private int ResolveUnspecifiedRouteChannelId()
    {
        var candidateChannelIds = new List<int>(_channelManager.ChannelCount) { 0 };

        for (int channelId = 1; channelId < _channelManager.ChannelCount; channelId++)
        {
            if (Volatile.Read(ref _explicitChannelSubscriberCounts[channelId]) == 0)
            {
                candidateChannelIds.Add(channelId);
            }
        }

        return _channelManager.SelectLeastLoadedChannelId(candidateChannelIds);
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

    private SubscriberInfo CreateSubscriberInfoFromDelegate<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options)
        where TEvent : notnull
    {
        var method = handler.Method;
        var target = handler.Target ?? handler;

        // 确定 Topic
        var topic = options?.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            // 如果未指定 Topic，使用事件类型全名
            topic = _keyProvider.GetKey(typeof(TEvent));
        }

        var threadOption = options?.ThreadOption ?? ThreadOption.BackgroundThread;
        ValidateThreadOption(threadOption);
        ValidateChannelId(options?.ChannelId);

        return new SubscriberInfo(
            eventType: typeof(TEvent),
            target: target,
            method: method,
            timeout: options?.Timeout,
            threadOption: threadOption,
            topic: topic,
            channelId: options?.ChannelId,
            isParameterless: false);
    }

    private SubscriberInfo CreateTopicSubscriberInfo(
        string topic,
        Func<object?, CancellationToken, ValueTask> handler,
        SubscribeOptions? options)
    {
        var method = handler.Method;
        var target = handler.Target ?? handler;
        var threadOption = options?.ThreadOption ?? ThreadOption.BackgroundThread;
        ValidateThreadOption(threadOption);
        ValidateChannelId(options?.ChannelId);

        return new SubscriberInfo(
            eventType: typeof(object),
            target: target,
            method: method,
            timeout: options?.Timeout,
            threadOption: threadOption,
            topic: topic,
            channelId: options?.ChannelId,
            isParameterless: false);
    }

    private List<SubscriberInfo> ScanSubscriberMethods(object subscriber)
    {
        var result = new List<SubscriberInfo>();
        var type = subscriber.GetType();

        // 获取所有带 SubscribeAttribute 的方法（包括基类）
        var methods = new List<MethodInfo>();
        var currentType = type;

        while (currentType != null && currentType != typeof(object))
        {
            var typeMethods = currentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.DeclaringType == currentType && m.GetCustomAttributes<SubscribeAttribute>().Any());

            methods.AddRange(typeMethods);
            currentType = currentType.BaseType;
        }

        foreach (var method in methods)
        {
            // 获取所有 Subscribe 特性（支持多个）
            var attributes = method.GetCustomAttributes<SubscribeAttribute>().ToList();
            var parameters = method.GetParameters();

            // 验证方法签名：允许 0-2 个参数
            if (parameters.Length > 2)
            {
                throw new InvalidOperationException(
                    $"方法 {type.Name}.{method.Name} 最多有 2 个参数（事件参数和可选的 CancellationToken）");
            }

            // 验证返回类型
            if (method.ReturnType != typeof(void) &&
                method.ReturnType != typeof(Task) &&
                method.ReturnType != typeof(ValueTask))
            {
                throw new InvalidOperationException(
                    $"方法 {type.Name}.{method.Name} 返回类型必须是 void、Task 或 ValueTask");
            }

            // 判断是否为无参方法（不含事件参数）
            var nonCancellationParams = parameters.Where(p => p.ParameterType != typeof(CancellationToken)).ToList();
            var isParameterless = nonCancellationParams.Count == 0;

            // 无参方法使用 object 作为事件类型占位
            var eventType = isParameterless
                ? typeof(object)
                : nonCancellationParams.First().ParameterType;

            // 为每个特性创建一个 SubscriberInfo
            foreach (var attribute in attributes)
            {
                ValidateThreadOption(attribute.ThreadOption);
                int? channelId = attribute.ChannelId >= 0 ? attribute.ChannelId : null;
                ValidateChannelId(channelId);

                // 确定 Topic
                var topic = attribute.Topic;
                if (string.IsNullOrEmpty(topic))
                {
                    if (isParameterless)
                    {
                        throw new InvalidOperationException(
                            $"无参方法 {type.Name}.{method.Name} 必须指定 Topic");
                    }
                    // 如果未指定 Topic，使用事件类型全名
                    topic = _keyProvider.GetKey(eventType);
                }

                var info = new SubscriberInfo(
                    eventType: eventType,
                    target: subscriber,
                    method: method,
                    timeout: attribute.Timeout > 0 ? TimeSpan.FromMilliseconds(attribute.Timeout) : null,
                    threadOption: attribute.ThreadOption,
                    topic: topic,
                    channelId: channelId,
                    isParameterless: isParameterless);

                result.Add(info);
            }
        }

        return result;
    }

    /// <summary>
    /// 添加过滤器
    /// </summary>
    public void AddFilter(IEventFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (Volatile.Read(ref _dispatchStarted) == 1)
        {
            throw new InvalidOperationException("事件分发已启动后不支持动态添加过滤器。请在构造或启动前注册。");
        }
        _filters.Add(filter);
        _filters.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    /// <summary>
    /// 添加拦截器
    /// </summary>
    public void AddInterceptor(IEventInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        if (Volatile.Read(ref _dispatchStarted) == 1)
        {
            throw new InvalidOperationException("事件分发已启动后不支持动态添加拦截器。请在构造或启动前注册。");
        }
        _interceptors.Add(interceptor);
        _interceptors.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private void TrackExplicitChannelSubscriber(SubscriberInfo subscriber)
    {
        if (!subscriber.ChannelId.HasValue)
        {
            return;
        }

        Interlocked.Increment(ref _explicitChannelSubscriberCounts[subscriber.ChannelId.Value]);
    }

    private void UntrackExplicitChannelSubscriber(SubscriberInfo subscriber)
    {
        if (!subscriber.ChannelId.HasValue)
        {
            return;
        }

        var channelId = subscriber.ChannelId.Value;
        var updated = Interlocked.Decrement(ref _explicitChannelSubscriberCounts[channelId]);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _explicitChannelSubscriberCounts[channelId], 0);
        }
    }

    private void ValidateThreadOption(ThreadOption threadOption)
    {
        if (threadOption == ThreadOption.UIThread && _uiSynchronizationContext is null)
        {
            throw new InvalidOperationException(
                "ThreadOption.UIThread 需要可用的 SynchronizationContext。请在 UI/Main 线程创建 EventBus，或通过构造函数传入 synchronizationContext。");
        }
    }

    private void ValidateChannelId(int? channelId)
    {
        if (channelId.HasValue)
        {
            _channelManager.ValidateExplicitChannelId(channelId.Value);
        }
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
            _cts.Dispose();
        }
    }

    #endregion
}
