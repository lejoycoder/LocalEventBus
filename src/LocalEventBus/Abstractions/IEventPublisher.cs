namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件发布器接口
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// 异步发布强类型事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="topic">发布主题（可选，null 时使用事件类型全名）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>发布任务</returns>
    ValueTask PublishAsync<TEvent>(
        TEvent @event,
        string? topic = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// 同步发布强类型事件（排队，不等待处理完成）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="topic">发布主题（可选，null 时使用事件类型全名）</param>
    void Publish<TEvent>(TEvent @event, string? topic = null)
        where TEvent : notnull;

    /// <summary>
    /// 发布纯 Topic 事件（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="eventData">事件数据（可选）</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask PublishAsync(
        string topic,
        object? eventData = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 同步发布纯 Topic 事件（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="eventData">事件数据（可选）</param>
    void Publish(
        string topic,
        object? eventData = null);
}
