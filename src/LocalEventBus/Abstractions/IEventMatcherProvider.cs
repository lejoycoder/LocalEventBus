namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件匹配器提供者
/// 负责管理多个匹配器并执行匹配逻辑
/// </summary>
public interface IEventMatcherProvider
{
    /// <summary>
    /// 判断发布的 Topic 是否匹配订阅的 Topic
    /// 遍历所有注册的匹配器，任一匹配即返回 true
    /// </summary>
    /// <param name="publishedTopic">发布的 Topic</param>
    /// <param name="subscribedTopic">订阅的 Topic（可能包含模式）</param>
    /// <returns>是否匹配</returns>
    bool IsMatch(string publishedTopic, string subscribedTopic);

    /// <summary>
    /// 获取所有注册的匹配器
    /// </summary>
    IReadOnlyList<IEventMatcher> Matchers { get; }
}
