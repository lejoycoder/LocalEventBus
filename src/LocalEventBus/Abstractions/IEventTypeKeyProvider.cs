namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件类型键提取器
/// 负责从 Type 提取字符串键用于 Topic
/// </summary>
public interface IEventTypeKeyProvider
{
    /// <summary>
    /// 从事件类型提取 Topic 键
    /// </summary>
    /// <param name="eventType">事件类型</param>
    /// <returns>事件类型键（字符串），用作 Topic</returns>
    string GetKey(Type eventType);
}
