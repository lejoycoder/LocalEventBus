using System.Collections.Immutable;
using System.Collections.Concurrent;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 事件订阅者注册表
/// 支持基于 Topic 的事件路由和模式匹配
/// </summary>
public sealed class EventSubscriberRegistry
{
    private readonly IEventMatcherProvider _matcherProvider;

    // 按 Topic 索引订阅者
    private readonly ConcurrentDictionary<string, ImmutableArray<SubscriberInfo>> _subscribersByTopic = new();

    // 所有订阅者的 Topic 列表（用于模式匹配查找）
    private ImmutableArray<string> _allTopics = ImmutableArray<string>.Empty;
    private readonly object _topicLock = new();

    /// <summary>
    /// 构造函数（依赖注入）
    /// </summary>
    /// <param name="matcherProvider">匹配器提供者</param>
    public EventSubscriberRegistry(IEventMatcherProvider matcherProvider)
    {
        _matcherProvider = matcherProvider ?? throw new ArgumentNullException(nameof(matcherProvider));
    }

    /// <summary>
    /// 获取匹配器提供者
    /// </summary>
    public IEventMatcherProvider MatcherProvider => _matcherProvider;

    /// <summary>
    /// 添加订阅者
    /// </summary>
    public void AddSubscriber(SubscriberInfo subscriber)
    {
        var topic = subscriber.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            throw new ArgumentException("订阅者必须有 Topic", nameof(subscriber));
        }

        _subscribersByTopic.AddOrUpdate(
            topic,
            _ => ImmutableArray.Create(subscriber),
            (_, existing) =>
            {
                // 按优先级排序插入
                var list = existing.ToList();
                int index = list.FindIndex(s => s.Priority < subscriber.Priority);
                if (index < 0)
                    list.Add(subscriber);
                else
                    list.Insert(index, subscriber);
                return list.ToImmutableArray();
            });

        // 更新 Topic 列表
        RebuildTopicList();
    }

    /// <summary>
    /// 移除订阅者
    /// </summary>
    public void RemoveSubscriber(SubscriberInfo subscriber)
    {
        var topic = subscriber.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            return;
        }

        _subscribersByTopic.AddOrUpdate(
            topic,
            _ => ImmutableArray<SubscriberInfo>.Empty,
            (_, existing) => existing.Remove(subscriber));

        // 清理空的 Topic
        if (_subscribersByTopic.TryGetValue(topic, out var subscribers) && subscribers.IsEmpty)
        {
            _subscribersByTopic.TryRemove(topic, out _);
        }

        // 更新 Topic 列表
        RebuildTopicList();
    }

    /// <summary>
    /// 移除实例的所有订阅
    /// </summary>
    public void RemoveSubscribersByTarget(object target)
    {
        var topicsToRemove = new List<string>();

        foreach (var kvp in _subscribersByTopic)
        {
            var filtered = kvp.Value.Where(s => !ReferenceEquals(s.Target, target)).ToImmutableArray();
            if (filtered.Length != kvp.Value.Length)
            {
                if (filtered.IsEmpty)
                {
                    topicsToRemove.Add(kvp.Key);
                }
                else
                {
                    _subscribersByTopic.TryUpdate(kvp.Key, filtered, kvp.Value);
                }
            }
        }

        foreach (var topic in topicsToRemove)
        {
            _subscribersByTopic.TryRemove(topic, out _);
        }

        RebuildTopicList();
    }

    /// <summary>
    /// 通过发布的 Topic 获取订阅者
    /// 支持精确匹配和模式匹配
    /// </summary>
    /// <param name="publishedTopic">发布的 Topic</param>
    /// <returns>匹配的订阅者列表</returns>
    public ImmutableArray<SubscriberInfo> GetSubscribersByPublishedTopic(string publishedTopic)
    {
        if (string.IsNullOrEmpty(publishedTopic))
        {
            return ImmutableArray<SubscriberInfo>.Empty;
        }

        var result = new List<SubscriberInfo>();
        var addedSubscribers = new HashSet<SubscriberInfo>();

        // 1. 快速路径：精确匹配（O(1)）
        if (_subscribersByTopic.TryGetValue(publishedTopic, out var exactMatches))
        {
            foreach (var subscriber in exactMatches)
            {
                if (addedSubscribers.Add(subscriber))
                {
                    result.Add(subscriber);
                }
            }
        }

        if (_matcherProvider.Matchers.Count > 0)
        {
            // 遍历所有订阅 Topic，检查是否匹配发布的 Topic
            var allTopics = _allTopics;
            foreach (var subscribedTopic in allTopics)
            {
                // 跳过已经精确匹配的
                if (subscribedTopic == publishedTopic)
                    continue;

                // 使用匹配器提供者判断
                if (_matcherProvider.IsMatch(publishedTopic, subscribedTopic))
                {
                    if (_subscribersByTopic.TryGetValue(subscribedTopic, out var matches))
                    {
                        foreach (var subscriber in matches)
                        {
                            if (addedSubscribers.Add(subscriber))
                            {
                                result.Add(subscriber);
                            }
                        }
                    }
                }
            }
        }

        // 按优先级排序
        result.Sort((a, b) => b.Priority.CompareTo(a.Priority));

        return result.ToImmutableArray();
    }

    /// <summary>
    /// 获取精确匹配的订阅者（不进行模式匹配）
    /// </summary>
    public ImmutableArray<SubscriberInfo> GetSubscribersByTopic(string topic)
    {
        return _subscribersByTopic.TryGetValue(topic, out var subscribers)
            ? subscribers
            : ImmutableArray<SubscriberInfo>.Empty;
    }

    /// <summary>
    /// 获取所有订阅者
    /// </summary>
    public IReadOnlyList<SubscriberInfo> GetAllSubscribers()
    {
        return _subscribersByTopic.Values
            .SelectMany(s => s)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 获取订阅者数量
    /// </summary>
    public int Count => _subscribersByTopic.Values.Sum(s => s.Length);

    /// <summary>
    /// 获取所有 Topic
    /// </summary>
    public IReadOnlyList<string> GetAllTopics() => _allTopics;

    /// <summary>
    /// 重建 Topic 列表（用于模式匹配）
    /// </summary>
    private void RebuildTopicList()
    {
        lock (_topicLock)
        {
            _allTopics = _subscribersByTopic.Keys.ToImmutableArray();
        }
    }
}
