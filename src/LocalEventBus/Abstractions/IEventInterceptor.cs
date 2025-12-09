using LocalEventBus.Internal;

namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件拦截器接口
/// </summary>
public interface IEventInterceptor
{
    /// <summary>
    /// 拦截器执行顺序（数字越小越先执行）
    /// </summary>
    int Order { get; }

    /// <summary>
    /// 处理前拦截
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="subscriber">订阅者信息</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask OnHandlingAsync<TEvent>(
        TEvent @event,
        SubscriberInfo subscriber,
        CancellationToken cancellationToken)
        where TEvent : notnull;

    /// <summary>
    /// 处理后拦截
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="subscriber">订阅者信息</param>
    /// <param name="elapsed">处理耗时</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask OnHandledAsync<TEvent>(
        TEvent @event,
        SubscriberInfo subscriber,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
        where TEvent : notnull;

    /// <summary>
    /// 处理失败拦截
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="subscriber">订阅者信息</param>
    /// <param name="exception">异常</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask OnHandlerFailedAsync<TEvent>(
        TEvent @event,
        SubscriberInfo subscriber,
        Exception exception,
        CancellationToken cancellationToken)
        where TEvent : notnull;
}
