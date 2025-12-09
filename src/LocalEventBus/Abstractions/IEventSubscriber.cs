namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件订阅器接口
/// </summary>
public interface IEventSubscriber
{
    /// <summary>
    /// 订阅事件（委托方式）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    /// <returns>订阅令牌（Dispose 时取消订阅）</returns>
    /// <remarks>
    /// 如果 options.Topic 为 null，则订阅 TEvent.FullName
    /// </remarks>
    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull;

    /// <summary>
    /// 订阅事件（同步委托方式）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    /// <returns>订阅令牌</returns>
    /// <remarks>
    /// 如果 options.Topic 为 null，则订阅 TEvent.FullName
    /// </remarks>
    IDisposable Subscribe<TEvent>(
        Action<TEvent> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull;

    /// <summary>
    /// 订阅事件（实例方式 - 扫描 SubscribeAttribute）
    /// </summary>
    /// <param name="subscriber">订阅者实例</param>
    /// <returns>订阅令牌</returns>
    IDisposable Subscribe(object subscriber);

    /// <summary>
    /// 订阅纯 Topic（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    /// <returns>订阅令牌</returns>
    IDisposable Subscribe(
        string topic,
        Func<object?, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null);

    /// <summary>
    /// 订阅纯 Topic（无强类型，同步）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    /// <returns>订阅令牌</returns>
    IDisposable Subscribe(
        string topic,
        Action<object?> handler,
        SubscribeOptions? options = null);

    /// <summary>
    /// 取消订阅实例
    /// </summary>
    /// <param name="subscriber">订阅者实例</param>
    void Unsubscribe(object subscriber);
}

/// <summary>
/// 订阅选项
/// </summary>
public sealed class SubscribeOptions
{
    /// <summary>
    /// 订阅的主题
    /// 如果为 null，则订阅事件类型全名（TEvent.FullName）
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 优先级 (0-10，数字越大优先级越高)
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发处理
    /// </summary>
    public bool AllowConcurrency { get; set; } = true;

    /// <summary>
    /// 处理超时时间
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
