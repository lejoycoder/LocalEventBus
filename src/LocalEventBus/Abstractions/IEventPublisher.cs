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
    /// <param name="options">发布选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>发布任务</returns>
    /// <remarks>
    /// 如果 options.Topic 为 null，则使用 TEvent.FullName 作为 Topic
    /// </remarks>
    ValueTask PublishAsync<TEvent>(
        TEvent @event,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// 同步发布强类型事件（排队，不等待处理完成）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="options">发布选项</param>
    /// <returns>是否成功入队</returns>
    /// <remarks>
    /// 如果 options.Topic 为 null，则使用 TEvent.FullName 作为 Topic
    /// </remarks>
    bool Publish<TEvent>(TEvent @event, PublishOptions? options = null)
        where TEvent : notnull;

    /// <summary>
    /// 批量发布强类型事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="events">事件集合</param>
    /// <param name="options">发布选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <remarks>
    /// 如果 options.Topic 为 null，则使用 TEvent.FullName 作为 Topic
    /// </remarks>
    ValueTask PublishBatchAsync<TEvent>(
        IEnumerable<TEvent> events,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// 发布纯 Topic 事件（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="eventData">事件数据（可选）</param>
    /// <param name="options">发布选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask PublishAsync(
        string topic,
        object? eventData = null,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 同步发布纯 Topic 事件（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="eventData">事件数据（可选）</param>
    /// <param name="options">发布选项</param>
    /// <returns>是否成功入队</returns>
    bool Publish(
        string topic,
        object? eventData = null,
        PublishOptions? options = null);
}

/// <summary>
/// 发布选项
/// </summary>
public sealed class PublishOptions
{
    /// <summary>
    /// 主题名称
    /// 如果为 null，则使用事件类型全名
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 分区键（相同分区键的事件保证有序处理）
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>
    /// 优先级 (0-10，默认 5)
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发执行订阅者
    /// </summary>
    public bool AllowConcurrency { get; set; } = false;

    /// <summary>
    /// 关联ID（用于链路追踪）
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// 超时时间
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
