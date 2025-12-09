
## 📋 设计目标

1. **基于 Topic 的事件路由系统**
2. **发布时支持强类型和纯 Topic 两种方式**
3. **一个方法可订阅多个 Topic**
4. **插件化的匹配器架构**
5. **高性能和可扩展性**

---

## 🎯 核心概念

### Topic（主题）

Topic 是事件路由的核心概念：

1. **自动 Topic**：发布时不指定 Topic，自动使用 `TEvent.FullName`
2. **手动 Topic**：发布时通过 `PublishOptions.Topic` 指定
3. **订阅 Topic**：通过 `[Subscribe(Topic = "...")]` 或 `SubscribeOptions.Topic` 指定
4. **Topic 匹配**：支持精确匹配、通配符、正则表达式等

### 发布方式

1. **强类型 + 自动 Topic**：
   ```csharp
   await eventBus.PublishAsync(new OrderCreatedEvent(...));
   // Topic = "MyApp.Events.OrderCreatedEvent"
   ```

2. **强类型 + 手动 Topic**：
   ```csharp
   await eventBus.PublishAsync(
       new OrderCreatedEvent(...),
       new PublishOptions { Topic = "order.created" });
   ```

3. **纯 Topic 发布**：
   ```csharp
   await eventBus.PublishAsync("system.heartbeat");
   await eventBus.PublishAsync("notification", new { Message = "Hello" });
   ```

### 订阅方式

1. **自动 Topic 订阅**：
   ```csharp
   [Subscribe]  // Topic = 事件类型全名
   public void Handle(OrderCreatedEvent e) { }
   ```

2. **手动 Topic 订阅**：
   ```csharp
   [Subscribe(Topic = "order.created")]
   public void Handle(OrderCreatedEvent e) { }
   ```

3. **多 Topic 订阅**：
   ```csharp
   [Subscribe(Topic = "order.created")]
   [Subscribe(Topic = "order.updated")]
   public void Handle(OrderEvent e) { }
   ```

---

## 🏗️ 核心架构

### 1. 事件类型键提取器接口

```csharp
namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件类型键提取器
/// 负责从 Type 提取字符串键用于匹配
/// </summary>
public interface IEventTypeKeyProvider
{
    /// <summary>
    /// 从事件类型提取键
    /// </summary>
    /// <param name="eventType">事件类型</param>
    /// <returns>事件类型键（字符串）</returns>
    string GetKey(Type eventType);
}
```

**默认实现：**

```csharp
namespace LocalEventBus.Internal;

/// <summary>
/// 默认事件类型键提取器
/// 使用类型全名作为键
/// </summary>
internal sealed class DefaultEventTypeKeyProvider : IEventTypeKeyProvider
{
    public string GetKey(Type eventType)
    {
        return eventType.FullName ?? eventType.Name;
    }
}
```

---

### 2. 事件匹配器接口

```csharp
namespace LocalEventBus.Abstractions;

/// <summary>
/// 事件匹配器
/// 负责判断事件键是否匹配订阅模式
/// </summary>
public interface IEventMatcher
{
    /// <summary>
    /// 判断事件键是否匹配订阅模式
    /// </summary>
    /// <param name="eventKey">事件类型键</param>
    /// <param name="subscriptionPattern">订阅模式</param>
    /// <returns>是否匹配</returns>
    bool IsMatch(string eventKey, string subscriptionPattern);
}
```

**默认实现（精确匹配）：**

```csharp
namespace LocalEventBus.Internal;

/// <summary>
/// 精确匹配器（默认）
/// 只支持完全相等的字符串匹配
/// </summary>
internal sealed class ExactEventMatcher : IEventMatcher
{
    public bool IsMatch(string eventKey, string subscriptionPattern)
    {
        return string.Equals(eventKey, subscriptionPattern, StringComparison.Ordinal);
    }
}
```

---

### 3. 通配符匹配器

```csharp
namespace LocalEventBus.Matchers;

/// <summary>
/// 通配符匹配器
/// 支持 * 和 ? 通配符
/// </summary>
public sealed class WildcardEventMatcher : IEventMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    private readonly bool _ignoreCase;

    public WildcardEventMatcher(bool ignoreCase = false)
    {
        _ignoreCase = ignoreCase;
    }

    public bool IsMatch(string eventKey, string subscriptionPattern)
    {
        // 快速路径：精确匹配
        if (string.Equals(eventKey, subscriptionPattern, 
            _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        // 检查是否包含通配符
        if (!subscriptionPattern.Contains('*') && !subscriptionPattern.Contains('?'))
        {
            return false;
        }

        // 转换为正则表达式并缓存
        var regex = _regexCache.GetOrAdd(subscriptionPattern, pattern =>
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            
            var options = RegexOptions.Compiled;
            if (_ignoreCase)
                options |= RegexOptions.IgnoreCase;
            
            return new Regex(regexPattern, options);
        });

        return regex.IsMatch(eventKey);
    }
}
```

---

### 4. 扩展包：正则表达式匹配器

```csharp
namespace LocalEventBus.Matchers;

/// <summary>
/// 正则表达式匹配器
/// 支持完整的正则表达式语法
/// </summary>
public sealed class RegexEventMatcher : IEventMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    private readonly RegexOptions _options;

    public RegexEventMatcher(RegexOptions options = RegexOptions.Compiled | RegexOptions.IgnoreCase)
    {
        _options = options;
    }

    public bool IsMatch(string eventKey, string subscriptionPattern)
    {
        // 快速路径：精确匹配
        if (eventKey == subscriptionPattern)
        {
            return true;
        }

        try
        {
            var regex = _regexCache.GetOrAdd(subscriptionPattern, pattern =>
                new Regex(pattern, _options));

            return regex.IsMatch(eventKey);
        }
        catch (ArgumentException)
        {
            // 无效的正则表达式，降级为精确匹配
            return eventKey == subscriptionPattern;
        }
    }
}
```

---

## 📦 数据结构重构

### 1. SubscriberInfo 简化

```csharp
namespace LocalEventBus.Internal;

public sealed class SubscriberInfo
{
    // 现有属性保持不变
    public Type EventType { get; init; }
    public object Target { get; init; }
    public MethodInfo Method { get; init; }
    public int Priority { get; init; } = 5;
    public bool AllowConcurrency { get; init; } = true;
    public TimeSpan? Timeout { get; init; }
    public bool IsParameterless { get; init; }
    
    // ✅ Topic 用于订阅和匹配
    /// <summary>
    /// 订阅的 Topic
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string Topic { get; init; }
    
    // ...其他现有属性和方法...
}
```

---

### 2. SubscribeAttribute 增强

```csharp
namespace LocalEventBus;

/// <summary>
/// 订阅特性
/// ✅ 一个方法可以添加多个 [Subscribe] 特性来订阅多个 Topic
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class SubscribeAttribute : Attribute
{
    /// <summary>
    /// 订阅的 Topic
    /// 如果为 null，则订阅方法参数类型的全名（Type.FullName）
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 优先级（0-10，数字越大优先级越高）
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发执行
    /// </summary>
    public bool AllowConcurrency { get; set; } = true;

    /// <summary>
    /// 超时时间（毫秒），0 表示使用默认超时
    /// </summary>
    public double Timeout { get; set; } = 0;
}
```

---

### 3. SubscribeOptions 简化

```csharp
namespace LocalEventBus.Abstractions;

public sealed class SubscribeOptions
{
    /// <summary>
    /// 订阅的 Topic
    /// 如果为 null，则订阅 TEvent 的全名（Type.FullName）
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string? Topic { get; set; }

    public int Priority { get; set; } = 5;
    public bool AllowConcurrency { get; set; } = true;
    public TimeSpan? Timeout { get; set; }
}
```

---

## 🗄️ 订阅者注册表重构

```csharp
namespace LocalEventBus.Internal;

internal sealed class EventSubscriberRegistry
{
    // 匹配器（通过构造函数注入）
    private readonly IEventMatcher _matcher;

    // ✅ 所有订阅者按 Topic 索引
    // Key: Topic（字符串）
    // Value: 订阅者列表
    private readonly ConcurrentDictionary<string, ImmutableList<SubscriberInfo>> _topicSubscribers = new();

    // ✅ 所有订阅者的 Topic 列表（用于模式匹配查找）
    private volatile ImmutableArray<string> _allTopics = ImmutableArray<string>.Empty;

    /// <summary>
    /// 构造函数
    /// </summary>
    public EventSubscriberRegistry(IEventMatcher matcher)
    {
        _matcher = matcher;
    }

    /// <summary>
    /// 添加订阅者
    /// </summary>
    public void AddSubscriber(SubscriberInfo subscriber)
    {
        _topicSubscribers.AddOrUpdate(
            subscriber.Topic,
            _ => ImmutableList.Create(subscriber),
            (_, list) => list.Add(subscriber));
        
        // 更新 Topic 列表
        RebuildTopicList();
    }

    /// <summary>
    /// 移除订阅者
    /// </summary>
    public void RemoveSubscriber(SubscriberInfo subscriber)
    {
        _topicSubscribers.AddOrUpdate(
            subscriber.Topic,
            _ => ImmutableList<SubscriberInfo>.Empty,
            (_, list) => list.Remove(subscriber));
        
        // 更新 Topic 列表
        RebuildTopicList();
    }

    /// <summary>
    /// ✅ 通过发布的 Topic 获取订阅者
    /// 支持精确匹配和模式匹配
    /// </summary>
    public ImmutableList<SubscriberInfo> GetSubscribersByPublishedTopic(string publishedTopic)
    {
        var result = ImmutableList<SubscriberInfo>.Empty;

        // 1. 快速路径：精确匹配
        if (_topicSubscribers.TryGetValue(publishedTopic, out var exactMatches))
        {
            result = result.AddRange(exactMatches);
        }

        // 2. ✅ 模式匹配：遍历所有订阅 Topic，检查是否匹配发布的 Topic
        foreach (var subscribedTopic in _allTopics)
        {
            // 跳过已经精确匹配的
            if (subscribedTopic == publishedTopic)
                continue;

            // 使用匹配器判断
            if (_matcher.IsMatch(publishedTopic, subscribedTopic))
            {
                if (_topicSubscribers.TryGetValue(subscribedTopic, out var matches))
                {
                    result = result.AddRange(matches);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 移除指定目标的所有订阅者
    /// </summary>
    public void RemoveSubscribersByTarget(object target)
    {
        foreach (var topic in _topicSubscribers.Keys.ToArray())
        {
            _topicSubscribers.AddOrUpdate(
                topic,
                _ => ImmutableList<SubscriberInfo>.Empty,
                (_, list) => list.RemoveAll(s => ReferenceEquals(s.Target, target)));
        }
        
        RebuildTopicList();
    }

    /// <summary>
    /// 获取所有订阅者
    /// </summary>
    public IReadOnlyList<SubscriberInfo> GetAllSubscribers()
    {
        return _topicSubscribers.Values
            .SelectMany(list => list)
            .Distinct()
            .ToArray();
    }

    public int Count => _topicSubscribers.Values.Sum(list => list.Count);

    /// <summary>
    /// 重建 Topic 列表（用于模式匹配）
    /// </summary>
    private void RebuildTopicList()
    {
        _allTopics = _topicSubscribers.Keys.ToImmutableArray();
    }
}
```

---

## ⚙️ 配置选项

```csharp
namespace LocalEventBus.Abstractions;

public sealed class EventBusOptions
{
    // 现有配置...
    public int ChannelCapacity { get; set; } = 0;
    public BoundedChannelFullMode ChannelFullMode { get; set; } = BoundedChannelFullMode.Wait;
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public int PartitionCount { get; set; } = 4;
    public RetryOptions RetryOptions { get; set; } = new();

    // ✅ 新增：事件类型键提取器
    /// <summary>
    /// 事件类型键提取器
    /// 用于从事件类型提取 Topic（当发布时未指定 Topic）
    /// 默认使用 DefaultEventTypeKeyProvider（类型全名）
    /// </summary>
    public IEventTypeKeyProvider? EventTypeKeyProvider { get; set; }

    // ✅ 新增：事件匹配器
    /// <summary>
    /// Topic 匹配器
    /// 用于判断发布的 Topic 是否匹配订阅的 Topic
    /// 默认使用 ExactEventMatcher（精确匹配）
    /// </summary>
    public IEventMatcher? EventMatcher { get; set; }
}
```

---

## 🔧 DefaultEventBus 集成

```csharp
namespace LocalEventBus.Internal;

public sealed class DefaultEventBus : IEventBus, IEventBusDiagnostics
{
    private readonly EventSubscriberRegistry _subscriberRegistry;
    private readonly EventChannelManager _channelManager;
    private readonly EventBusOptions _options;
    private readonly IEventTypeKeyProvider _keyProvider;
    private readonly IEventMatcher _matcher;
    // ...其他字段...

    public DefaultEventBus(EventBusOptions? options = null)
    {
        _options = options ?? new EventBusOptions();
        
        // ✅ 初始化键提取器和匹配器
        _keyProvider = _options.EventTypeKeyProvider ?? new DefaultEventTypeKeyProvider();
        _matcher = _options.EventMatcher ?? new ExactEventMatcher();
        
        // ✅ 注入匹配器到订阅者注册表
        _subscriberRegistry = new EventSubscriberRegistry(_matcher);
        _channelManager = new EventChannelManager(_options);
    }

    // ✅ ScanSubscriberMethods 需要更新以支持多个 Subscribe 特性
    private List<SubscriberInfo> ScanSubscriberMethods(object subscriber)
    {
        var result = new List<SubscriberInfo>();
        var type = subscriber.GetType();
        var methods = /* 扫描方法... */;

        foreach (var method in methods)
        {
            // ✅ 获取所有 Subscribe 特性（支持多个）
            var attributes = method.GetCustomAttributes<SubscribeAttribute>();
            if (!attributes.Any())
                continue;

            var parameters = method.GetParameters();
            // ...验证逻辑...

            var eventType = /* 确定事件类型... */;
            var isParameterless = parameters.Length == 0;

            // ✅ 为每个特性创建一个 SubscriberInfo
            foreach (var attribute in attributes)
            {
                // 确定 Topic
                var topic = attribute.Topic;
                if (string.IsNullOrEmpty(topic))
                {
                    // 如果未指定 Topic，使用事件类型全名
                    topic = _keyProvider.GetKey(eventType);
                }

                var info = new SubscriberInfo
                {
                    EventType = eventType,
                    Target = subscriber,
                    Method = method,
                    Priority = attribute.Priority,
                    AllowConcurrency = attribute.AllowConcurrency,
                    Timeout = attribute.Timeout > 0 ? TimeSpan.FromMilliseconds(attribute.Timeout) : null,
                    Topic = topic,
                    IsParameterless = isParameterless
                };

                result.Add(info);
            }
        }

        return result;
    }

    // ✅ CreateSubscriberInfoFromDelegate 需要更新
    private SubscriberInfo CreateSubscriberInfoFromDelegate<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options)
        where TEvent : notnull
    {
        var method = handler.Method;
        var target = handler.Target ?? handler;

        // 确定 Topic
        var topic = options?.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            // 如果未指定 Topic，使用事件类型全名
            topic = _keyProvider.GetKey(typeof(TEvent));
        }

        return new SubscriberInfo
        {
            EventType = typeof(TEvent),
            Target = target,
            Method = method,
            Priority = options?.Priority ?? 5,
            AllowConcurrency = options?.AllowConcurrency ?? true,
            Timeout = options?.Timeout,
            Topic = topic,
            IsParameterless = false
        };
    }

    // ✅ PublishAsync 需要更新以支持 Topic
    public async ValueTask PublishAsync<TEvent>(
        TEvent @event, 
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull
    {
        // 确定发布的 Topic
        var topic = options?.Topic;
        if (string.IsNullOrEmpty(topic))
        {
            // ✅ 如果未指定 Topic，使用事件类型全名
            topic = _keyProvider.GetKey(typeof(TEvent));
        }

        // 创建事件信封
        var envelope = new EventEnvelope
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventData = @event,
            EventType = typeof(TEvent),
            Topic = topic,  // ✅ 添加 Topic
            Timestamp = DateTimeOffset.UtcNow,
            // ...其他属性...
        };

        // 获取订阅者
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        // ...后续处理...
    }

    // ✅ 新增：支持纯 Topic 发布（无类型参数）
    public async ValueTask PublishAsync(
        string topic,
        object? eventData = null,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        // 创建事件信封
        var envelope = new EventEnvelope
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventData = eventData ?? new object(),
            EventType = eventData?.GetType() ?? typeof(object),
            Topic = topic,
            Timestamp = DateTimeOffset.UtcNow,
            // ...其他属性...
        };

        // 获取订阅者
        var subscribers = _subscriberRegistry.GetSubscribersByPublishedTopic(topic);

        // ...后续处理...
    }
}
```

---

## 📖 使用示例

### 1. 基本使用（自动 Topic）

```csharp
// 默认使用精确匹配
var eventBus = EventBusFactory.Create();

// ✅ 订阅特定事件（不指定 Topic，自动使用事件类型全名）
[Subscribe]
public void HandleOrderCreated(OrderCreatedEvent e)
{
    Console.WriteLine($"订单 {e.OrderId} 已创建");
}

// ✅ 发布事件（不指定 Topic，自动使用 OrderCreatedEvent 的全名）
// Topic = "MyApp.Events.OrderCreatedEvent"
await eventBus.PublishAsync(new OrderCreatedEvent(123, "张三", 99.99m));
```

### 2. 指定 Topic 发布和订阅

```csharp
// ✅ 订阅指定的 Topic
[Subscribe(Topic = "order.created")]
public void HandleOrderCreated(OrderCreatedEvent e)
{
    Console.WriteLine($"订单 {e.OrderId} 已创建");
}

// ✅ 发布到指定的 Topic
await eventBus.PublishAsync(
    new OrderCreatedEvent(123, "张三", 99.99m),
    new PublishOptions { Topic = "order.created" });
```

### 3. 一个方法订阅多个 Topic

```csharp
// ✅ 一个方法可以添加多个 [Subscribe] 特性
[Subscribe(Topic = "order.created")]
[Subscribe(Topic = "order.updated")]
[Subscribe(Topic = "order.cancelled")]
public void HandleOrderEvents(OrderEvent e)
{
    Console.WriteLine($"订单事件: {e.OrderId}, 类型: {e.GetType().Name}");
}

// 发布不同事件
await eventBus.PublishAsync(
    new OrderCreatedEvent(...),
    new PublishOptions { Topic = "order.created" });
    
await eventBus.PublishAsync(
    new OrderUpdatedEvent(...),
    new PublishOptions { Topic = "order.updated" });
```

### 4. 纯 Topic 发布（无强类型）

```csharp
// ✅ 订阅纯字符串 Topic
[Subscribe(Topic = "system.notification")]
public void HandleNotification(object data)
{
    Console.WriteLine($"收到通知: {data}");
}

// ✅ 发布纯 Topic 事件（无需强类型）
await eventBus.PublishAsync("system.notification", new { Message = "系统升级通知" });

// ✅ 甚至可以不传递数据
await eventBus.PublishAsync("system.heartbeat");
```

### 5. 通配符匹配

```csharp
// 使用通配符匹配器
var eventBus = EventBusFactory.Create(new EventBusOptions
{
    EventMatcher = new WildcardEventMatcher()
});

// ✅ 订阅所有订单相关的 Topic
[Subscribe(Topic = "order.*")]
public void HandleAllOrderEvents(object eventData)
{
    Console.WriteLine($"订单事件: {eventData.GetType().Name}");
}

// 发布不同的订单事件
await eventBus.PublishAsync(
    new OrderCreatedEvent(...),
    new PublishOptions { Topic = "order.created" });
    
await eventBus.PublishAsync(
    new OrderCancelledEvent(...),
    new PublishOptions { Topic = "order.cancelled" });

// ✅ 两个都会被 HandleAllOrderEvents 捕获
```

### 6. 正则表达式匹配

```csharp
// 使用正则表达式匹配器
var eventBus = EventBusFactory.Create(new EventBusOptions
{
    EventMatcher = new RegexEventMatcher()
});

// ✅ 订阅符合正则的 Topic
[Subscribe(Topic = @"^(order|payment)\.(created|updated)$")]
public void HandleImportantEvents(object eventData)
{
    _logger.LogInformation("重要事件: {Type}", eventData.GetType().Name);
}

// 匹配的 Topic：
// - order.created
// - order.updated
// - payment.created
// - payment.updated

// 不匹配的 Topic：
// - order.cancelled
// - user.created
```

### 7. 混合使用自动和手动 Topic

```csharp
public class OrderService
{
    // ✅ 自动 Topic：订阅 OrderCreatedEvent 的全名
    [Subscribe]
    public void HandleOrderCreated(OrderCreatedEvent e)
    {
        Console.WriteLine($"处理订单创建: {e.OrderId}");
    }

    // ✅ 手动 Topic：订阅自定义 Topic
    [Subscribe(Topic = "order.important")]
    public void HandleImportantOrder(OrderCreatedEvent e)
    {
        Console.WriteLine($"重要订单: {e.OrderId}");
    }
}

// 发布方式1：自动 Topic（使用 OrderCreatedEvent 的全名）
await eventBus.PublishAsync(new OrderCreatedEvent(...));
// → 触发 HandleOrderCreated

// 发布方式2：手动 Topic
await eventBus.PublishAsync(
    new OrderCreatedEvent(...),
    new PublishOptions { Topic = "order.important" });
// → 触发 HandleImportantOrder
```

### 8. 自定义键提取器

```csharp
// 自定义键提取器：使用短名称
public class ShortNameKeyProvider : IEventTypeKeyProvider
{
    public string GetKey(Type eventType)
    {
        return eventType.Name; // 只返回类名，不包含命名空间
    }
}

var eventBus = EventBusFactory.Create(new EventBusOptions
{
    EventTypeKeyProvider = new ShortNameKeyProvider()
});

// ✅ 订阅时不指定 Topic，自动使用短名称
[Subscribe]  // Topic = "OrderCreatedEvent"（短名称）
public void HandleOrder(OrderCreatedEvent e)
{
    Console.WriteLine($"订单: {e.OrderId}");
}

// ✅ 发布时不指定 Topic，自动使用短名称
// Topic = "OrderCreatedEvent"
await eventBus.PublishAsync(new OrderCreatedEvent(...));
```

---

## 📊 性能优化策略

### 1. 快速路径优化

```csharp
public ImmutableList<SubscriberInfo> GetSubscribersByPublishedTopic(string publishedTopic)
{
    var result = ImmutableList<SubscriberInfo>.Empty;

    // ✅ 快速路径：精确匹配（O(1)）
    if (_topicSubscribers.TryGetValue(publishedTopic, out var exactMatches))
    {
        result = result.AddRange(exactMatches);
    }

    // ✅ 优化：如果只有精确匹配订阅者，跳过模式匹配
    if (_allTopics.Length <= 1)
    {
        return result;
    }

    // 模式匹配：遍历所有订阅 Topic
    foreach (var subscribedTopic in _allTopics)
    {
        if (subscribedTopic == publishedTopic)
            continue;

        if (_matcher.IsMatch(publishedTopic, subscribedTopic))
        {
            if (_topicSubscribers.TryGetValue(subscribedTopic, out var matches))
            {
                result = result.AddRange(matches);
            }
        }
    }

    return result;
}
```

### 2. Topic 缓存在事件信封中

```csharp
// ✅ 在 EventEnvelope 中缓存 Topic，避免重复提取
internal sealed class EventEnvelope
{
    public string EventId { get; init; }
    public object EventData { get; init; }
    public Type EventType { get; init; }
    
    // ✅ 缓存 Topic，避免多次查找
    public string Topic { get; init; }
    
    public DateTimeOffset Timestamp { get; init; }
    // ...其他属性...
}

// 创建时确定 Topic 并缓存
private EventEnvelope CreateEventEnvelope<TEvent>(
    TEvent @event, 
    PublishOptions? options)
{
    var topic = options?.Topic;
    if (string.IsNullOrEmpty(topic))
    {
        topic = _keyProvider.GetKey(typeof(TEvent));
    }

    return new EventEnvelope
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventData = @event,
        EventType = typeof(TEvent),
        Topic = topic,  // ✅ 缓存 Topic
        Timestamp = DateTimeOffset.UtcNow,
        // ...
    };
}
```

### 3. 匹配器缓存

```csharp
// WildcardEventMatcher 使用正则缓存
public sealed class WildcardEventMatcher : IEventMatcher
{
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public bool IsMatch(string eventKey, string subscriptionPattern)
    {
        // 快速路径：精确匹配
        if (eventKey == subscriptionPattern)
            return true;

        // 检查是否包含通配符
        if (!subscriptionPattern.Contains('*') && !subscriptionPattern.Contains('?'))
            return false;

        // ✅ 缓存编译后的正则表达式
        var regex = _regexCache.GetOrAdd(subscriptionPattern, pattern =>
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.Compiled);
        });

        return regex.IsMatch(eventKey);
    }
}
```

---

## 🔌 扩展包组织

```
LocalEventBus/                          # 核心包
├── Abstractions/
│   ├── IEventTypeKeyProvider.cs       # 接口
│   └── IEventMatcher.cs               # 接口
└── Internal/
    ├── DefaultEventTypeKeyProvider.cs  # 默认实现
    └── ExactEventMatcher.cs            # 默认实现

LocalEventBus.Matchers/                 # 扩展包
├── WildcardEventMatcher.cs             # 通配符匹配器
├── RegexEventMatcher.cs                # 正则匹配器
└── CombinedEventMatcher.cs             # 组合匹配器（可选）
```

---

## 📝 完整 API 定义

### IEventPublisher 接口

```csharp
namespace LocalEventBus.Abstractions;

public interface IEventPublisher
{
    /// <summary>
    /// 发布强类型事件
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="event">事件数据</param>
    /// <param name="options">发布选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <remarks>
    /// 如果 options.Topic 为 null，则使用 TEvent.FullName 作为 Topic
    /// </remarks>
    ValueTask PublishAsync<TEvent>(
        TEvent @event,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;

    /// <summary>
    /// ✅ 发布纯 Topic 事件（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="eventData">事件数据（可选）</param>
    /// <param name="options">发布选项</param>
    /// <param name="cancellationToken">取消令牌</param>
    ValueTask PublishAsync(
        string topic,
        object? eventData = null,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default);
}
```

### IEventSubscriber 接口

```csharp
namespace LocalEventBus.Abstractions;

public interface IEventSubscriber
{
    /// <summary>
    /// 订阅事件（扫描订阅者对象的所有 [Subscribe] 方法）
    /// </summary>
    IDisposable Subscribe(object subscriber);

    /// <summary>
    /// 订阅事件（使用委托）
    /// </summary>
    /// <typeparam name="TEvent">事件类型</typeparam>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    /// <remarks>
    /// 如果 options.Topic 为 null，则订阅 TEvent.FullName
    /// </remarks>
    IDisposable Subscribe<TEvent>(
        Func<TEvent, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null)
        where TEvent : notnull;

    /// <summary>
    /// ✅ 订阅纯 Topic（无强类型）
    /// </summary>
    /// <param name="topic">主题名称</param>
    /// <param name="handler">事件处理器</param>
    /// <param name="options">订阅选项</param>
    IDisposable Subscribe(
        string topic,
        Func<object, CancellationToken, ValueTask> handler,
        SubscribeOptions? options = null);

    /// <summary>
    /// 取消订阅
    /// </summary>
    void Unsubscribe(object subscriber);
}
```

### PublishOptions

```csharp
namespace LocalEventBus.Abstractions;

public sealed class PublishOptions
{
    /// <summary>
    /// ✅ 主题名称
    /// 如果为 null，则使用事件类型全名
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 分区键（用于负载均衡）
    /// </summary>
    public int? PartitionKey { get; set; }

    /// <summary>
    /// 是否等待所有处理器完成
    /// </summary>
    public bool WaitForCompletion { get; set; } = false;

    /// <summary>
    /// 超时时间
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
```

### SubscribeOptions

```csharp
namespace LocalEventBus.Abstractions;

public sealed class SubscribeOptions
{
    /// <summary>
    /// ✅ 订阅的主题
    /// 如果为 null，则订阅事件类型全名
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 优先级（0-10，数字越大优先级越高）
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发执行
    /// </summary>
    public bool AllowConcurrency { get; set; } = true;

    /// <summary>
    /// 超时时间
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
```

### SubscribeAttribute

```csharp
namespace LocalEventBus;

/// <summary>
/// 订阅特性
/// ✅ AllowMultiple = true：支持一个方法订阅多个 Topic
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class SubscribeAttribute : Attribute
{
    /// <summary>
    /// ✅ 订阅的主题
    /// 如果为 null，则订阅方法参数类型的全名
    /// 支持模式匹配（取决于配置的 IEventMatcher）
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 优先级（0-10，数字越大优先级越高）
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发执行
    /// </summary>
    public bool AllowConcurrency { get; set; } = true;

    /// <summary>
    /// 超时时间（毫秒），0 表示使用默认超时
    /// </summary>
    public double Timeout { get; set; } = 0;
}
```

---

## ✅ 总结

### 核心优势

1. **✅ 灵活的发布方式**：
   - 强类型发布：`PublishAsync<TEvent>(event)`，自动使用类型全名作为 Topic
   - 强类型 + Topic：`PublishAsync<TEvent>(event, new PublishOptions { Topic = "custom" })`
   - 纯 Topic 发布：`PublishAsync("topic", data)`，无需强类型

2. **✅ 多订阅支持**：
   - 一个方法可以添加多个 `[Subscribe]` 特性
   - 每个特性指定不同的 Topic
   - 支持自动 Topic（使用事件类型全名）

3. **✅ 灵活的匹配模式**：
   - 默认：精确匹配（高性能，O(1)）
   - 通配符：`order.*`、`*.created`
   - 正则表达式：`^(order|payment)\.(created|updated)$`
   - 自定义：实现 `IEventMatcher`

4. **✅ 完全可扩展**：
   - `IEventTypeKeyProvider`：自定义 Topic 提取逻辑
   - `IEventMatcher`：自定义 Topic 匹配逻辑
   - 插件化架构，核心包轻量

5. **✅ 高性能**：
   - 精确匹配 O(1)
   - 模式匹配按需启用
   - 正则缓存优化

### 设计原则

- **简化设计**：移除 EventTypePattern，统一使用 Topic
- **灵活发布**：支持强类型和纯 Topic 两种发布方式
- **多订阅支持**：一个方法可以处理多个 Topic
- **精确匹配优先**：默认精确匹配保证性能
- **按需扩展**：通配符、正则等高级功能通过扩展包提供
- **依赖注入配置**：通过 `EventBusOptions` 注入自定义实现

### 核心改进

1. ✅ **移除 EventTypePattern**：简化设计，统一使用 Topic
2. ✅ **AllowMultiple = true**：支持一个方法订阅多个 Topic
3. ✅ **自动 Topic**：发布时不指定 Topic 自动使用 TEvent 全名
4. ✅ **纯 Topic 发布**：支持 `PublishAsync(string topic, object? data = null)`

### 接口更新

```csharp
public interface IEventPublisher
{
    // 强类型发布（可选 Topic）
    ValueTask PublishAsync<TEvent>(
        TEvent @event, 
        PublishOptions? options = null,
        CancellationToken cancellationToken = default)
        where TEvent : notnull;

    // ✅ 新增：纯 Topic 发布
    ValueTask PublishAsync(
        string topic,
        object? eventData = null,
        PublishOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class PublishOptions
{
    // ✅ Topic：指定发布的主题
    public string? Topic { get; set; }
    
    // 其他现有选项...
    public int? PartitionKey { get; set; }
    public bool WaitForCompletion { get; set; }
    public TimeSpan? Timeout { get; set; }
}
```