namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件 Topic 匹配器
/// 负责判断发布的 Topic 是否匹配订阅的 Topic
/// </summary>
public interface IEventMatcher
{
    /// <summary>
    /// 判断发布的 Topic 是否匹配订阅的 Topic
    /// </summary>
    /// <param name="publishedTopic">发布的 Topic</param>
    /// <param name="subscribedTopic">订阅的 Topic（可能包含模式）</param>
    /// <returns>是否匹配</returns>
    bool IsMatch(string publishedTopic, string subscribedTopic);
}
