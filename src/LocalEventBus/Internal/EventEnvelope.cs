namespace LocalEventBus.Internal;

/// <summary>
/// 事件路由受众
/// </summary>
public enum EventRouteAudience
{
    /// <summary>
    /// 显式通道订阅者（ChannelId=1..N-1）
    /// </summary>
    Explicit = 0,

    /// <summary>
    /// 未指定通道订阅者（ChannelId=null）
    /// </summary>
    Unspecified = 1
}

/// <summary>
/// 事件信封（包装事件数据）
/// </summary>
public sealed class EventEnvelope
{
    /// <summary>
    /// 事件数据
    /// </summary>
    public required object? EventData { get; init; }

    /// <summary>
    /// 事件类型
    /// </summary>
    public required Type EventType { get; init; }

    /// <summary>
    /// 事件时间戳
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 发布通道编号
    /// </summary>
    public int ChannelId { get; init; }

    /// <summary>
    /// 路由受众
    /// </summary>
    public EventRouteAudience RouteAudience { get; init; } = EventRouteAudience.Explicit;

    /// <summary>
    /// 主题
    /// </summary>
    public string? Topic { get; init; }

}
