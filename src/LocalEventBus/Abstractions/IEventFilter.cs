namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件过滤器接口
/// </summary>
public interface IEventFilter
{
    /// <summary>
    /// 过滤器执行顺序（数字越小越先执行）
    /// </summary>
    int Order { get; }

    /// <summary>
    /// 判断事件是否应该被处理
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件实例</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>true 表示应该处理，false 表示跳过</returns>
    ValueTask<bool> ShouldProcessAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : notnull;
}
