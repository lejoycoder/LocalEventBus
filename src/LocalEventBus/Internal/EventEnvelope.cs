namespace LocalEventBus.Internal;

/// <summary>
/// 事件信封（包装事件数据）
/// </summary>
public sealed class EventEnvelope
{
    /// <summary>
    /// 事件唯一标识
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// 事件数据
    /// </summary>
    public required object EventData { get; init; }

    /// <summary>
    /// 事件类型
    /// </summary>
    public required Type EventType { get; init; }

    /// <summary>
    /// 事件时间戳
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 分区键
    /// </summary>
    public string? PartitionKey { get; init; }

    /// <summary>
    /// 优先级
    /// </summary>
    public int Priority { get; init; } = 5;

    /// <summary>
    /// 关联ID
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// 主题
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>
    /// 是否允许并发执行
    /// </summary>
    public bool AllowConcurrency { get; init; }
}
