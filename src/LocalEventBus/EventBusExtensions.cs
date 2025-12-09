using LocalEventBus.Abstractions;
using LocalEventBus.Internal;

namespace LocalEventBus;

/// <summary>
/// 事件总线扩展方法
/// </summary>
public static class EventBusExtensions
{
    /// <summary>
    /// 发布事件
    /// </summary>
    public static ValueTask PublishAsync<TEvent>(this IEventPublisher publisher, TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return publisher.PublishAsync(@event, null, cancellationToken);
    }

    /// <summary>
    /// 发布事件到指定主题
    /// </summary>
    public static ValueTask PublishToTopicAsync<TEvent>(
        this IEventPublisher publisher,
        TEvent @event,
        string topic,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        return publisher.PublishAsync(@event, new PublishOptions { Topic = topic }, cancellationToken);
    }

    /// <summary>
    /// 发布事件
    /// </summary>
    public static bool PublishToTopic<TEvent>(this IEventPublisher publisher, TEvent @event, string topic)
        where TEvent : notnull
    {
        return publisher.Publish(@event, new PublishOptions { Topic = topic });
    }

    /// <summary>
    /// 订阅事件
    /// </summary>
    public static IDisposable Subscribe<TEvent>(
        this IEventSubscriber subscriber,
        Func<TEvent, ValueTask> handler)
        where TEvent : notnull
    {
        return subscriber.Subscribe<TEvent>((e, _) => handler(e), null);
    }

    /// <summary>
    /// 订阅事件
    /// </summary>
    public static IDisposable Subscribe<TEvent>(
        this IEventSubscriber subscriber,
        Func<TEvent, Task> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull
    {
        return subscriber.Subscribe<TEvent>(async (e, _) =>
        {
            await handler(e);
        }, options);
    }

    /// <summary>
    /// 订阅指定主题的事件
    /// </summary>
    public static IDisposable SubscribeToTopic<TEvent>(
        this IEventSubscriber subscriber,
        string topic,
        Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : notnull
    {
        return subscriber.Subscribe(handler, new SubscribeOptions { Topic = topic });
    }

    /// <summary>
    /// 订阅指定主题的事件（同步版本）
    /// </summary>
    public static IDisposable SubscribeToTopic<TEvent>(
        this IEventSubscriber subscriber,
        string topic,
        Action<TEvent> handler)
        where TEvent : notnull
    {
        return subscriber.Subscribe(handler, new SubscribeOptions { Topic = topic });
    }
}
