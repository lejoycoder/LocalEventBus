using LocalEventBus.Abstractions;
using LocalEventBus.Internal;

namespace LocalEventBus;

/// <summary>
/// 事件总线工厂
/// </summary>
public static class EventBusFactory
{
    /// <summary>
    /// 创建新的事件总线实例
    /// </summary>
    /// <param name="options">配置选项</param>
    /// <returns>事件总线实例</returns>
    public static IEventBus Create(EventBusOptions? options = null)
    {
        return new DefaultEventBus(options);
    }

    /// <summary>
    /// 创建带配置的事件总线实例
    /// </summary>
    /// <param name="configure">配置委托</param>
    /// <returns>事件总线实例</returns>
    public static IEventBus Create(Action<EventBusOptions> configure)
    {
        var options = new EventBusOptions();
        configure(options);
        return new DefaultEventBus(options);
    }
}
