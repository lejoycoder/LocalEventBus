using System.Collections.Concurrent;
using System.Reflection;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

public sealed partial class DefaultEventBus
{
    private readonly record struct SubscribeAttributeDefinition(
        string? Topic,
        double Timeout,
        ThreadOption ThreadOption,
        int ChannelId);

    private readonly record struct SubscriberMethodDefinition(
        MethodInfo Method,
        Type EventType,
        bool IsParameterless,
        IReadOnlyList<SubscribeAttributeDefinition> Attributes);

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<SubscriberMethodDefinition>> SubscriberMethodCache = new();

    #region IEventSubscriber Implementation

    public IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(subscriber);
        var subscribersToRemove = _subscriberRegistry.GetSubscribersByTarget(subscriber);

        _subscriberRegistry.RemoveSubscribersByTarget(subscriber);

        foreach (var item in subscribersToRemove)
        {
            UntrackExplicitChannelSubscriber(item);
        }
    }

    #endregion

    #region Subscription Helpers

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
        var type = subscriber.GetType();
        var methodDefinitions = SubscriberMethodCache.GetOrAdd(type, BuildSubscriberMethodDefinitions);
        var result = new List<SubscriberInfo>(methodDefinitions.Count);

        foreach (var methodDefinition in methodDefinitions)
        {
            foreach (var attribute in methodDefinition.Attributes)
            {
                ValidateThreadOption(attribute.ThreadOption);
                int? channelId = attribute.ChannelId >= 0 ? attribute.ChannelId : null;
                ValidateChannelId(channelId);

                var topic = attribute.Topic;
                if (string.IsNullOrEmpty(topic))
                {
                    if (methodDefinition.IsParameterless)
                    {
                        throw new InvalidOperationException(
                            $"无参方法 {type.Name}.{methodDefinition.Method.Name} 必须指定 Topic");
                    }

                    topic = _keyProvider.GetKey(methodDefinition.EventType);
                }

                result.Add(new SubscriberInfo(
                    eventType: methodDefinition.EventType,
                    target: subscriber,
                    method: methodDefinition.Method,
                    timeout: attribute.Timeout > 0 ? TimeSpan.FromMilliseconds(attribute.Timeout) : null,
                    threadOption: attribute.ThreadOption,
                    topic: topic,
                    channelId: channelId,
                    isParameterless: methodDefinition.IsParameterless));
            }
        }

        return result;
    }

    private static IReadOnlyList<SubscriberMethodDefinition> BuildSubscriberMethodDefinitions(Type subscriberType)
    {
        var result = new List<SubscriberMethodDefinition>();
        var currentType = subscriberType;

        while (currentType != null && currentType != typeof(object))
        {
            foreach (var method in currentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.DeclaringType != currentType)
                {
                    continue;
                }

                var attributes = method.GetCustomAttributes<SubscribeAttribute>().ToArray();
                if (attributes.Length == 0)
                {
                    continue;
                }

                ValidateSubscriberMethodSignature(subscriberType, method, out var eventType, out var isParameterless);

                var attributeDefinitions = new SubscribeAttributeDefinition[attributes.Length];
                for (int i = 0; i < attributes.Length; i++)
                {
                    var attribute = attributes[i];
                    attributeDefinitions[i] = new SubscribeAttributeDefinition(
                        Topic: attribute.Topic,
                        Timeout: attribute.Timeout,
                        ThreadOption: attribute.ThreadOption,
                        ChannelId: attribute.ChannelId);
                }

                result.Add(new SubscriberMethodDefinition(
                    Method: method,
                    EventType: eventType,
                    IsParameterless: isParameterless,
                    Attributes: attributeDefinitions));
            }

            currentType = currentType.BaseType;
        }

        return result;
    }

    private static void ValidateSubscriberMethodSignature(
        Type subscriberType,
        MethodInfo method,
        out Type eventType,
        out bool isParameterless)
    {
        var parameters = method.GetParameters();

        if (parameters.Length > 2)
        {
            throw new InvalidOperationException(
                $"方法 {subscriberType.Name}.{method.Name} 最多有 2 个参数（事件参数和可选的 CancellationToken）");
        }

        if (method.ReturnType != typeof(void) &&
            method.ReturnType != typeof(Task) &&
            method.ReturnType != typeof(ValueTask))
        {
            throw new InvalidOperationException(
                $"方法 {subscriberType.Name}.{method.Name} 返回类型必须是 void、Task 或 ValueTask");
        }

        eventType = typeof(object);
        isParameterless = true;

        for (int i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            if (parameterType == typeof(CancellationToken))
            {
                continue;
            }

            if (isParameterless)
            {
                eventType = parameterType;
                isParameterless = false;
            }
        }
    }

    /// <summary>
    /// 添加过滤器
    /// </summary>
    public void AddFilter(IEventFilter filter)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filter);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _dispatchStarted) == 1)
            {
                throw new InvalidOperationException("事件分发已启动后不支持动态添加过滤器。请在构造或启动前注册。");
            }

            _filters.Add(filter);
            _filters.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    /// <summary>
    /// 添加拦截器
    /// </summary>
    public void AddInterceptor(IEventInterceptor interceptor)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _dispatchStarted) == 1)
            {
                throw new InvalidOperationException("事件分发已启动后不支持动态添加拦截器。请在构造或启动前注册。");
            }

            _interceptors.Add(interceptor);
            _interceptors.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    private void TrackExplicitChannelSubscriber(SubscriberInfo subscriber)
    {
        if (!subscriber.ChannelId.HasValue)
        {
            return;
        }

        _channelManager.RegisterExplicitSubscriber(subscriber.ChannelId.Value);
    }

    private void UntrackExplicitChannelSubscriber(SubscriberInfo subscriber)
    {
        if (!subscriber.ChannelId.HasValue)
        {
            return;
        }

        _channelManager.UnregisterExplicitSubscriber(subscriber.ChannelId.Value);
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

    #endregion
}
