namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件总线接口 - 组合发布和订阅
/// </summary>
public interface IEventBus : IEventPublisher, IEventSubscriber, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 直接调用订阅者
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>调用任务</returns>
    ValueTask InvokeAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// 通过 Topic 直接调用订阅者
    /// </summary>
    /// <param name="topic">主题</param>
    /// <param name="eventData">可选的事件数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>调用任务</returns>
    ValueTask InvokeByTopicAsync(string topic, object? eventData = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 事件总线诊断接口
/// </summary>
public interface IEventBusDiagnostics
{
    /// <summary>
    /// 获取当前订阅者数量
    /// </summary>
    int GetSubscriberCount();

    /// <summary>
    /// 获取待处理事件数量
    /// </summary>
    int GetPendingEventCount();

    /// <summary>
    /// 获取所有订阅者信息
    /// </summary>
    IReadOnlyList<Internal.SubscriberInfo> GetSubscribers();

    /// <summary>
    /// 获取指定事件类型的订阅者
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    IReadOnlyList<Internal.SubscriberInfo> GetSubscribers<TEvent>() where TEvent : notnull;
}
