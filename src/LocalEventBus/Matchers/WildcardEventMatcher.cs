using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Matchers;

/// <summary>
/// 通配符匹配器
/// 支持 * 和 ? 通配符
/// </summary>
public sealed class WildcardEventMatcher : IEventMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    private readonly bool _ignoreCase;

    /// <summary>
    /// 创建通配符匹配器
    /// </summary>
    /// <param name="ignoreCase">是否忽略大小写</param>
    public WildcardEventMatcher(bool ignoreCase = false)
    {
        _ignoreCase = ignoreCase;
    }

    public bool IsMatch(string publishedTopic, string subscribedTopic)
    {
        // 快速路径：精确匹配
        if (string.Equals(publishedTopic, subscribedTopic,
            _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        // 检查是否包含通配符
        if (!subscribedTopic.Contains('*') && !subscribedTopic.Contains('?'))
        {
            return false;
        }

        // 转换为正则表达式并缓存
        var regex = _regexCache.GetOrAdd(subscribedTopic, pattern =>
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            var options = RegexOptions.Compiled;
            if (_ignoreCase)
                options |= RegexOptions.IgnoreCase;

            return new Regex(regexPattern, options, TimeSpan.FromSeconds(1));
        });

        return regex.IsMatch(publishedTopic);
    }
}
