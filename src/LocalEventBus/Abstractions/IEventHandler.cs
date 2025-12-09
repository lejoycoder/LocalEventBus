namespace LocalEventBus.Abstractions;

/// <summary>
/// 强类型事件处理器接口
/// </summary>
/// <typeparam name="TEvent">事件类型</typeparam>
public interface IEventHandler<in TEvent> where TEvent : notnull
{
    /// <summary>
    /// 处理事件
    /// </summary>
    /// <param name="event">事件实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
