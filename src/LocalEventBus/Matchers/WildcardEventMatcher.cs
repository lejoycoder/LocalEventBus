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
        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // 发布方未使用通配符时，按字面量比较
        if (!HasWildcard(publishedTopic))
        {
            return string.Equals(publishedTopic, subscribedTopic, comparison);
        }

        // 仅允许发布方模式去匹配订阅 Topic
        var regex = _regexCache.GetOrAdd(publishedTopic, CreateRegexFromWildcard);
        return regex.IsMatch(subscribedTopic);
    }

    private Regex CreateRegexFromWildcard(string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        var options = RegexOptions.Compiled;
        if (_ignoreCase)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(regexPattern, options, TimeSpan.FromSeconds(1));
    }

    private static bool HasWildcard(string text) => text.Contains('*') || text.Contains('?');
}
