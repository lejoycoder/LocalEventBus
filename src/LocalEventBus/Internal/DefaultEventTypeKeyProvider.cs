using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 默认事件类型键提取器
/// 使用类型全名作为键
/// </summary>
internal sealed class DefaultEventTypeKeyProvider : IEventTypeKeyProvider
{
    public string GetKey(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return eventType.FullName ?? eventType.Name;
    }
}
