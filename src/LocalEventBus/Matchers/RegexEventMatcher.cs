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
        // 快速路径：精确匹配
        if (publishedTopic == subscribedTopic)
        {
            return true;
        }

        try
        {
            var regex = _regexCache.GetOrAdd(subscribedTopic, pattern =>
                new Regex(pattern, _options, TimeSpan.FromSeconds(1)));

            return regex.IsMatch(publishedTopic);
        }
        catch (ArgumentException)
        {
            // 无效的正则表达式，降级为精确匹配
            return publishedTopic == subscribedTopic;
        }
        catch (RegexMatchTimeoutException)
        {
            // 正则匹配超时，返回 false
            return false;
        }
    }
}
