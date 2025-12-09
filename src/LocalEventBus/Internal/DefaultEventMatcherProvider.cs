using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 默认事件匹配器提供者
/// 管理多个匹配器，支持链式匹配
/// </summary>
public sealed class DefaultEventMatcherProvider : IEventMatcherProvider
{
    private readonly List<IEventMatcher> _matchers;

    /// <summary>
    /// 创建默认匹配器提供者
    /// </summary>
    /// <param name="matchers">匹配器集合</param>
    public DefaultEventMatcherProvider(IEnumerable<IEventMatcher>? matchers = null)
    {
        _matchers = matchers?.ToList() ?? [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<IEventMatcher> Matchers => _matchers;

    /// <inheritdoc/>
    public bool IsMatch(string publishedTopic, string subscribedTopic)
    {
        if (_matchers.Count == 0)
            return false;

        // 快速路径：精确匹配
        if (string.Equals(publishedTopic, subscribedTopic, StringComparison.Ordinal))
        {
            return true;
        }

        // 遍历所有匹配器，任一匹配即返回 true
        foreach (var matcher in _matchers)
        {
            if (matcher.IsMatch(publishedTopic, subscribedTopic))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 添加匹配器
    /// </summary>
    public void AddMatcher(IEventMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        _matchers.Add(matcher);
    }

    /// <summary>
    /// 移除匹配器
    /// </summary>
    public bool RemoveMatcher(IEventMatcher matcher)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        return _matchers.Remove(matcher);
    }
}
