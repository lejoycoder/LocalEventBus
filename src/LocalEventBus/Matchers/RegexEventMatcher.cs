using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Matchers;

/// <summary>
/// 正则表达式匹配器
/// 支持完整的正则表达式语法
/// </summary>
public sealed class RegexEventMatcher : IEventMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    private readonly RegexOptions _options;

    /// <summary>
    /// 创建正则表达式匹配器
    /// </summary>
    /// <param name="options">正则表达式选项</param>
    public RegexEventMatcher(RegexOptions options = RegexOptions.Compiled | RegexOptions.IgnoreCase)
    {
        _options = options;
    }

    public bool IsMatch(string publishedTopic, string subscribedTopic)
    {
        // 快速路径：发布方未使用正则模式时，仅做字面量比较
        if (!HasRegexMeta(publishedTopic))
        {
            return string.Equals(publishedTopic, subscribedTopic, StringComparison.Ordinal);
        }

        try
        {
            // 仅允许发布方模式去匹配订阅 Topic
            var regex = _regexCache.GetOrAdd(publishedTopic, pattern =>
                new Regex(pattern, _options, TimeSpan.FromSeconds(1)));

            return regex.IsMatch(subscribedTopic);
        }
        catch (ArgumentException)
        {
            // 无效的正则表达式，降级为字面量比较
            return string.Equals(publishedTopic, subscribedTopic, StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException)
        {
            // 正则匹配超时，返回 false
            return false;
        }
    }

    // 粗略判断是否包含常见的正则元字符，用于区分纯文本与模式
    private static bool HasRegexMeta(string text) =>
        text.IndexOfAny(new[] { '.', '*', '?', '+', '|', '\\', '^', '$', '[', ']', '(', ')', '{', '}' }) >= 0;
}
