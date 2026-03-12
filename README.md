# LocalEventBus

[![NuGet](https://img.shields.io/nuget/v/LocalEventBus.svg)](https://www.nuget.org/packages/LocalEventBus)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

一个高性能、轻量级的本地事件总线库，专为 .NET 8+ 设计。

## ✨ 特性

- 🚀 **高性能** - 使用 Expression 树编译委托，比反射快 15 倍
- 🔒 **线程安全** - 使用 `ConcurrentDictionary` + `ImmutableArray` 无锁设计
- 📦 **强类型** - 支持泛型事件，编译时类型检查
- 🎯 **Topic 路由** - 支持基于 Topic 的事件路由和灵活匹配
- 🔄 **重试机制** - 支持固定/线性/指数退避重试策略
- 🎯 **事件过滤** - 按条件过滤事件
- 🔌 **拦截器** - 支持前置、后置、失败拦截，用于监控、日志和异常处理
- ⚡ **异步优先** - 基于 `Channel<T>` 的异步事件队列
- 🔀 **订阅驱动通道路由** - 0号未指定通道 + 显式通道隔离，支持按负载自动分流
- 📍 **直接调用** - 支持 `InvokeAsync` 直接调用订阅者，无需通过事件队列
- 🔧 **依赖注入** - 原生支持 Microsoft.Extensions.DependencyInjection
- 🎨 **灵活匹配** - 支持精确匹配、通配符（*）、正则表达式匹配

## 📦 安装

```bash
dotnet add package LocalEventBus
```

## 🚀 快速开始

### 1. 创建事件总线

#### 使用工厂模式

```csharp
using LocalEventBus;
using LocalEventBus.Abstractions;

// 使用默认配置
var eventBus = EventBusFactory.Create();

// 或使用自定义配置
var eventBus = EventBusFactory.Create(options =>
{
    options.ChannelCapacity = 1000;
    options.DefaultTimeout = TimeSpan.FromSeconds(30);
    options.PartitionCount = 4;  // 配置通道数量（最小2）
    options.RetryOptions.MaxRetryAttempts = 3;
    options.RetryOptions.DelayStrategy = RetryDelayStrategy.ExponentialBackoff;
});
```

#### 使用依赖注入（推荐）

```csharp
using LocalEventBus;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

// 添加事件总线服务
services.AddLocalEventBus(options =>
{
    options.ChannelCapacity = 1000;
    options.DefaultTimeout = TimeSpan.FromSeconds(30);
    options.PartitionCount = 4;  // 配置通道数量（最小2）
    options.RetryOptions.MaxRetryAttempts = 3;
    options.RetryOptions.DelayStrategy = RetryDelayStrategy.ExponentialBackoff;
});

// 可选：添加通配符匹配器
services.AddLocalEventBus()
    .AddWildcardMatcher()
    .AddRegexMatcher();

var serviceProvider = services.BuildServiceProvider();
var eventBus = serviceProvider.GetRequiredService<IEventBus>();
```

### 2. 定义事件

```csharp
// 使用 record 定义事件（推荐）
public record OrderCreatedEvent(int OrderId, string CustomerName, decimal Amount);

public record PaymentReceivedEvent(int OrderId, decimal Amount, DateTime PaidAt);
```

### 3. 发布事件

```csharp
// 异步发布（推荐）
await eventBus.PublishAsync(new OrderCreatedEvent(123, "张三", 99.99m));

// 同步发布（入队不等待处理）
eventBus.Publish(new OrderCreatedEvent(456, "李四", 199.99m));

// 连续发布多个事件（按需自行循环）
var orders = new[]
{
    new OrderCreatedEvent(1, "Customer1", 100m),
    new OrderCreatedEvent(2, "Customer2", 200m),
    new OrderCreatedEvent(3, "Customer3", 300m)
};
foreach (var order in orders)
{
    await eventBus.PublishAsync(order);
}

// 直接调用（不经过队列，同步等待处理完成）
await eventBus.InvokeAsync(new OrderCreatedEvent(100, "DirectCall", 500m));

// 通过 Topic 直接调用
await eventBus.InvokeByTopicAsync("system/shutdown");
```

### 4. 订阅事件

#### 委托方式

```csharp
// 异步处理
var subscription = eventBus.Subscribe<OrderCreatedEvent>(async (order, ct) =>
{
    Console.WriteLine($"收到订单: {order.OrderId}, 客户: {order.CustomerName}");
    await ProcessOrderAsync(order, ct);
});

// 同步处理
eventBus.Subscribe<OrderCreatedEvent>(order =>
{
    Console.WriteLine($"订单 {order.OrderId} 已创建");
});

// 取消订阅
subscription.Dispose();
```

#### 特性方式

```csharp
public class OrderHandler
{
    // 带事件参数的订阅
    [Subscribe]
    public async ValueTask HandleOrderCreated(OrderCreatedEvent e, CancellationToken ct)
    {
        Console.WriteLine($"处理订单: {e.OrderId}");
        await Task.Delay(100, ct);
    }

    [Subscribe]
    public void HandlePayment(PaymentReceivedEvent e)
    {
        Console.WriteLine($"收到支付: {e.Amount}");
    }

    // 无参订阅者（必须指定 Topic）
    [Subscribe("system/shutdown")]
    public void OnSystemShutdown()
    {
        Console.WriteLine("系统即将关闭...");
    }

    // 无参订阅者 + CancellationToken
    [Subscribe("system/refresh")]
    public async ValueTask OnRefreshAsync(CancellationToken ct)
    {
        await RefreshCacheAsync(ct);
    }
}

// 注册订阅者
var handler = new OrderHandler();
var subscription = eventBus.Subscribe(handler);

// 取消所有订阅
subscription.Dispose();
```

## ⚙️ 配置选项

### EventBusOptions

```csharp
var eventBus = EventBusFactory.Create(options =>
{
    // 通道容量（0 = 无界通道）
    options.ChannelCapacity = 10000;
    
    // 通道满时的行为
    options.ChannelFullMode = BoundedChannelFullMode.Wait;
    
    // 默认处理超时
    options.DefaultTimeout = TimeSpan.FromSeconds(30);
    
    // 通道数量（最小2）
    // - 0号通道：未指定通道
    // - 1..N-1：显式通道
    // - 低负载（< 1K 事件/秒）：4-8
    // - 中等负载（1K-10K 事件/秒）：16
    // - 高负载（> 10K 事件/秒）：32-64
    options.PartitionCount = 16;
    
    // 重试配置
    options.RetryOptions.MaxRetryAttempts = 3;
    options.RetryOptions.InitialDelay = TimeSpan.FromSeconds(1);
    options.RetryOptions.MaxDelay = TimeSpan.FromSeconds(30);
    options.RetryOptions.DelayStrategy = RetryDelayStrategy.ExponentialBackoff;
});
```

### SubscribeOptions

```csharp
eventBus.Subscribe<OrderCreatedEvent>(handler, new SubscribeOptions
{
    // 主题名称（可选，不指定则使用事件类型全名）
    Topic = "orders/created",

    // 显式订阅通道（可选，范围 1..PartitionCount-1）
    // null 表示未指定通道订阅
    ChannelId = 1,
    
    // 处理超时（默认使用 EventBusOptions.DefaultTimeout）
    Timeout = TimeSpan.FromSeconds(10)
});
```

### 发布时指定 Topic

```csharp
// 强类型事件：可直接在发布时指定 topic
await eventBus.PublishAsync(
    new OrderCreatedEvent(123, "Alice", 100m),
    "orders/created");

// 无强类型事件：通过 Topic 发布
await eventBus.PublishByTopicAsync(
    "orders/created",
    new OrderCreatedEvent(123, "Alice", 100m));
```

### SubscribeAttribute

```csharp
public class MyHandler
{
    // 使用主题过滤
    [Subscribe("orders/vip")]
    public void HandleVipOrder(OrderCreatedEvent e) { }

    // 设置超时
    [Subscribe(Timeout = 5000)]
    public void HandleOrder(OrderCreatedEvent e) { }
}
```

## 🔌 高级功能

### 事件过滤器

```csharp
public class VipOnlyFilter : IEventFilter
{
    public int Order => 1;

    public ValueTask<bool> ShouldProcessAsync<TEvent>(TEvent @event, CancellationToken ct)
        where TEvent : notnull
    {
        if (@event is OrderCreatedEvent order)
        {
            return ValueTask.FromResult(order.Amount > 1000);
        }
        return ValueTask.FromResult(true);
    }
}

// 添加过滤器
eventBus.AddFilter(new VipOnlyFilter());
```

### 事件拦截器

```csharp
public class LoggingInterceptor : IEventInterceptor
{
    public int Order => 0;

    public ValueTask OnHandlingAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, CancellationToken ct)
        where TEvent : notnull
    {
        Console.WriteLine($"[开始] 处理事件: {typeof(TEvent).Name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnHandledAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, TimeSpan elapsed, CancellationToken ct)
        where TEvent : notnull
    {
        Console.WriteLine($"[完成] 处理事件: {typeof(TEvent).Name}, 耗时: {elapsed.TotalMilliseconds}ms");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnHandlerFailedAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, Exception ex, CancellationToken ct)
        where TEvent : notnull
    {
        Console.WriteLine($"[失败] 处理事件: {typeof(TEvent).Name}, 错误: {ex.Message}");
        return ValueTask.CompletedTask;
    }
}

// 添加拦截器
eventBus.AddInterceptor(new LoggingInterceptor());
```

### 订阅驱动通道路由

LocalEventBus 采用“订阅驱动”的通道模型：
- `0` 号通道固定为未指定通道
- `1..PartitionCount-1` 为显式通道（订阅时指定）
- 发布侧无需指定通道，框架根据匹配订阅者自动决定写入目标通道

```csharp
var eventBus = EventBusFactory.Create(options =>
{
    options.PartitionCount = 8; // 最小2，0号保留为未指定通道
});

// 显式通道订阅
eventBus.Subscribe<OrderCreatedEvent>(
    handler: order => Console.WriteLine($"C1: {order.OrderId}"),
    options: new SubscribeOptions { ChannelId = 1 });

eventBus.Subscribe<OrderCreatedEvent>(
    handler: order => Console.WriteLine($"C2: {order.OrderId}"),
    options: new SubscribeOptions { ChannelId = 2 });

// 未指定通道订阅（ChannelId = null）
eventBus.Subscribe<OrderCreatedEvent>(order => Console.WriteLine($"Default: {order.OrderId}"));

// 发布时无需传通道参数，框架会按匹配订阅者进行路由
await eventBus.PublishAsync(new OrderCreatedEvent(123, "Alice", 100m));
```

**路由规则：**
- ✅ 命中显式通道订阅者：按通道去重后分别入队（可跨通道并行）
- ✅ 命中未指定订阅者：写入“未指定通道池”，按最小负载自动选择（并列随机）
- ✅ 未指定通道池 = `{0} ∪ {当前未被显式订阅占用的通道}`
- ✅ 无匹配订阅者时直接跳过入队

### 直接调用订阅者（InvokeAsync）

除了通过事件队列异步处理，还可以直接同步调用订阅者：

```csharp
// 直接调用订阅者（不经过事件队列）
await eventBus.InvokeAsync(new OrderCreatedEvent(123, "Alice", 100m));

// 通过 Topic 直接调用
await eventBus.InvokeByTopicAsync("system/shutdown");

// 带事件数据的 Topic 调用
await eventBus.InvokeByTopicAsync("user/notification", 
    new NotificationEvent("Welcome!"));
```

**适用场景：**
- 需要立即执行的操作（不希望异步延迟）
- 需要等待目标订阅者完成
- 测试场景下验证订阅者行为

**注意事项：**
- `InvokeAsync` / `InvokeByTopicAsync` 要求最多一个匹配订阅者
- 当匹配到多个订阅者时会直接抛出 `InvalidOperationException`，且不会调用任何订阅者
- ✅ **会应用过滤器**（可以过滤不需要处理的事件）
- ❌ **不会触发拦截器**（OnHandlingAsync、OnHandledAsync、OnHandlerFailedAsync 都不会被调用）
- ✅ **支持超时机制**（会应用 SubscribeOptions.Timeout 或 EventBusOptions.DefaultTimeout）
- ❌ **不支持重试机制**（异常会直接抛出，需要调用方自行处理）

**与 PublishAsync 的区别：**

| 特性 | PublishAsync | InvokeAsync |
|------|--------------|-------------|
| 执行方式 | 异步队列 | 同步直接调用 |
| 过滤器 | ✅ 支持 | ✅ 支持 |
| 拦截器 | ✅ 支持 | ❌ 不支持 |
| 超时 | ✅ 支持 | ✅ 支持 |
| 重试 | ✅ 支持 | ❌ 不支持 |
| 异常处理 | 拦截器处理 | 调用方处理 |
| 顺序保证 | ✅ 同通道 FIFO | ❌ 不保证执行顺序 |

### Topic 匹配器

LocalEventBus 支持灵活的 Topic 匹配机制，可以实现精确匹配、通配符匹配和正则表达式匹配。

#### 精确匹配（默认）

```csharp
// 发布事件
await eventBus.PublishByTopicAsync("orders/created", new OrderCreatedEvent(...));

// 订阅事件（精确匹配）
[Subscribe(Topic = "orders/created")]
public void HandleOrderCreated(OrderCreatedEvent e) { }
```

#### 通配符匹配

```csharp
// 注册通配符匹配器
services.AddLocalEventBus()
    .AddWildcardMatcher();

// 订阅示例（订阅方使用字面量 Topic）
[Subscribe(Topic = "orders/created")]
public void HandleOrderCreated(object e) { }

[Subscribe(Topic = "orders/updated")]
public void HandleOrderUpdated(object e) { }

[Subscribe(Topic = "users/created")]
public void HandleUserCreated(object e) { }

// 发布侧使用通配符匹配多个订阅者
await eventBus.PublishByTopicAsync("orders/*");  // 命中 orders/created、orders/updated
await eventBus.PublishByTopicAsync("*/created"); // 命中 orders/created、users/created
await eventBus.PublishByTopicAsync("**");        // 命中所有订阅
```

> 仅发布方 Topic 支持通配符匹配订阅方：发布时可使用 `*` / `?` 将同一消息广播到符合模式的订阅者；订阅方 Topic 默认按字面量匹配（若需更灵活可使用正则匹配器）。

```csharp
// 发布侧使用通配符广播
await eventBus.PublishByTopicAsync("orders/*");     // 触发 orders/created、orders/updated 等订阅
await eventBus.PublishByTopicAsync("user-?/login"); // 触发 user-a/login、user-b/login 等订阅
```

#### 正则表达式匹配

```csharp
// 注册正则匹配器
services.AddLocalEventBus()
    .AddRegexMatcher();

// 订阅示例（订阅方使用字面量 Topic）
[Subscribe(Topic = "orders/created")]
public void HandleOrderCreated(object e) { }

[Subscribe(Topic = "orders/updated")]
public void HandleOrderUpdated(object e) { }

// 发布侧使用正则模式匹配多个订阅
await eventBus.PublishByTopicAsync(@"^orders/(created|updated)$");
await eventBus.PublishByTopicAsync(@"^user-\d+/login$");
```

#### 自定义匹配器

```csharp
// 实现自定义匹配器
public class CustomMatcher : IEventMatcher
{
    public int Order => 100;  // 执行顺序（越小越先执行）
    
    public bool IsMatch(string publishedTopic, string subscribedPattern)
    {
        // 自定义匹配逻辑
        return publishedTopic.Contains(subscribedPattern);
    }
}

// 注册自定义匹配器
services.AddLocalEventBus()
    .AddMatcher<CustomMatcher>();
```

### 诊断接口

```csharp
// 获取诊断信息
if (eventBus is IEventBusDiagnostics diagnostics)
{
    Console.WriteLine($"订阅者数量: {diagnostics.GetSubscriberCount()}");
    Console.WriteLine($"待处理事件: {diagnostics.GetPendingEventCount()}");
    
    foreach (var subscriber in diagnostics.GetSubscribers<OrderCreatedEvent>())
    {
        Console.WriteLine($"  - {subscriber}");
    }
}
```

## 💡 最佳实践

### 1. 合理配置通道数量

```csharp
// 根据负载和CPU核心数选择合适的通道数量
var eventBus = EventBusFactory.Create(options =>
{
    // 最小值 2（0号未指定通道 + 至少1个显式通道）
    options.PartitionCount = 2;

    // 多核并发（推荐：CPU核心数的2-4倍）
    // 例如：8核CPU可以设置为16-32
    options.PartitionCount = Environment.ProcessorCount * 2;
});
```

### 2. 使用显式通道做隔离

```csharp
// 将关键业务绑定到显式通道 1
eventBus.Subscribe<UserLoginEvent>(
    e => HandleUserLogin(e),
    new SubscribeOptions { ChannelId = 1 });

eventBus.Subscribe<UserLogoutEvent>(
    e => HandleUserLogout(e),
    new SubscribeOptions { ChannelId = 1 });

// 发布侧无需关心通道，框架按订阅者自动路由
await eventBus.PublishAsync(new UserLoginEvent(userId));
await eventBus.PublishAsync(new UserLogoutEvent(userId));
```

### 3. 使用拦截器统一处理日志和监控

```csharp
public class MonitoringInterceptor : IEventInterceptor
{
    private readonly ILogger _logger;
    private readonly IMetrics _metrics;
    
    public int Order => 0;
    
    public ValueTask OnHandlingAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, CancellationToken ct)
        where TEvent : notnull
    {
        _logger.LogInformation("开始处理事件: {EventType}", typeof(TEvent).Name);
        _metrics.IncrementCounter("event_handling_started");
        return ValueTask.CompletedTask;
    }
    
    public ValueTask OnHandledAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, TimeSpan elapsed, CancellationToken ct)
        where TEvent : notnull
    {
        _logger.LogInformation("事件处理完成: {EventType}, 耗时: {Elapsed}ms", 
            typeof(TEvent).Name, elapsed.TotalMilliseconds);
        _metrics.RecordHistogram("event_handling_duration", elapsed.TotalMilliseconds);
        return ValueTask.CompletedTask;
    }
    
    public ValueTask OnHandlerFailedAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, Exception ex, CancellationToken ct)
        where TEvent : notnull
    {
        _logger.LogError(ex, "事件处理失败: {EventType}", typeof(TEvent).Name);
        _metrics.IncrementCounter("event_handling_failed");
        return ValueTask.CompletedTask;
    }
}

// 注册拦截器
services.AddLocalEventBus()
    .AddInterceptor<MonitoringInterceptor>();
```

### 4. 优雅关闭

```csharp
// 在应用关闭时，确保所有事件都被处理完毕
public async Task ShutdownAsync(IEventBus eventBus, CancellationToken ct)
{
    // 1. 停止接收新事件（如果有入口控制）
    // ...
    
    // 2. 等待当前队列中的事件处理完成
    if (eventBus is IEventBusDiagnostics diagnostics)
    {
        while (diagnostics.GetPendingEventCount() > 0)
        {
            await Task.Delay(100, ct);
        }
    }
    
    // 3. 释放资源
    await eventBus.DisposeAsync();
}
```

### 5. 错误处理策略

```csharp
// 方式1：使用拦截器处理异常
public class ErrorHandlingInterceptor : IEventInterceptor
{
    public int Order => -100;  // 优先执行
    
    public ValueTask OnHandlerFailedAsync<TEvent>(TEvent @event, SubscriberInfo subscriber, Exception ex, CancellationToken ct)
        where TEvent : notnull
    {
        // 根据异常类型决定处理策略
        if (ex is TimeoutException)
        {
            // 超时异常，可能需要报警
            AlertSystem.SendAlert($"Event handler timeout: {typeof(TEvent).Name}");
        }
        else if (ex is InvalidOperationException)
        {
            // 业务异常，记录日志即可
            _logger.LogWarning(ex, "Business exception in event handler");
        }
        
        return ValueTask.CompletedTask;
    }
}

// 方式2：在订阅者中使用 try-catch
[Subscribe]
public async ValueTask HandleOrderCreated(OrderCreatedEvent e, CancellationToken ct)
{
    try
    {
        await ProcessOrderAsync(e, ct);
    }
    catch (Exception ex)
    {
        // 处理异常，避免影响其他订阅者
        _logger.LogError(ex, "Failed to process order: {OrderId}", e.OrderId);
    }
}
```

## 📊 性能特性

### 核心优化

| 优化项 | 技术方案 | 性能提升 |
|--------|----------|----------|
| 订阅者调用 | Expression 树编译委托 | 比反射快 15 倍 |
| 并发控制 | ConcurrentDictionary + ImmutableArray | 无锁设计，零拷贝 |
| 事件分发 | Channel<T> 异步队列 | 高吞吐量，低延迟 |
| 通道路由 | 订阅驱动通道路由（固定通道数） | 并发性能提升 2-5 倍 |
| 内存分配 | 不可变集合 + 对象池 | 极低 GC 压力 |

### 性能指标（参考值）

| 场景 | 吞吐量 | 平均延迟 | P99 延迟 |
|------|--------|----------|----------|
| 单订阅者 | ~100K ops/s | ~10μs | ~50μs |
| 10 订阅者 | ~50K ops/s | ~20μs | ~100μs |
| 100 订阅者 | ~10K ops/s | ~100μs | ~500μs |
| 多通道模式（16通道） | ~200K ops/s | ~5μs | ~30μs |

*测试环境: .NET 8.0, Intel i7-12700, 16GB RAM, Windows 11*

### 基准测试示例

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

[MemoryDiagnoser]
public class EventBusBenchmark
{
    private IEventBus _eventBus = null!;
    
    [GlobalSetup]
    public void Setup()
    {
        _eventBus = EventBusFactory.Create(options =>
        {
            options.PartitionCount = 16;
            options.ChannelCapacity = 10000;
        });
        
        // 注册订阅者
        _eventBus.Subscribe<TestEvent>(e => { /* 处理逻辑 */ });
    }
    
    [Benchmark]
    public async Task PublishAsync_1K_Events()
    {
        for (int i = 0; i < 1000; i++)
        {
            await _eventBus.PublishAsync(new TestEvent(i));
        }
    }
    
    [Benchmark]
    public void Publish_1K_Events()
    {
        for (int i = 0; i < 1000; i++)
        {
            _eventBus.Publish(new TestEvent(i));
        }
    }
}

// 运行基准测试
// BenchmarkRunner.Run<EventBusBenchmark>();
```

## 🏗️ 架构

```
┌─────────────────────────────────────────────────────────────┐
│                        IEventBus                            │
│  ┌──────────────────┐          ┌─────────────────────┐     │
│  │ IEventPublisher  │          │  IEventSubscriber   │     │
│  └────────┬─────────┘          └──────────┬──────────┘     │
└───────────┼────────────────────────────────┼────────────────┘
            │                                │
            ▼                                ▼
┌───────────────────────────────────────────────────────────────┐
│                     DefaultEventBus                           │
│  ┌──────────────────────────────────────────────────────────┐│
│  │  EventSubscriberRegistry (ConcurrentDict + ImmutableArray)││
│  └──────────────────────────────────────────────────────────┘│
│  ┌──────────────────────────────────────────────────────────┐│
│  │  EventChannelManager (Channel<EventEnvelope>)            ││
│  └──────────────────────────────────────────────────────────┘│
│  ┌──────────────────────────────────────────────────────────┐│
│  │  Pipeline: Filter → Interceptor → Invoker → Retry        ││
│  └──────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────┘
            │
            ▼
┌─────────────────────────────────┐
│  异常由拦截器处理 (Interceptor)  │
└─────────────────────────────────┘
```


## 🔧 要求

- .NET 8.0 或更高版本
- Microsoft.Extensions.DependencyInjection.Abstractions 8.0+
- System.Collections.Immutable 8.0+
- System.Threading.Channels 8.0+

## ❓ FAQ

### Q: PublishAsync 和 InvokeAsync 有什么区别？
**A:** `PublishAsync` 将事件放入队列异步处理，支持拦截器和重试；`InvokeAsync` 直接同步调用订阅者，不触发拦截器，适用于需要立即执行的场景。

### Q: 如何保证事件的处理顺序？
**A:** 通道内是 FIFO 顺序处理。显式通道订阅者按绑定通道顺序消费；未指定订阅者在“未指定通道池”中按负载选路并保持各通道内顺序。

### Q: 通道数量（PartitionCount）应该设置为多少？
**A:** 
- 最小值：2（`0` 号未指定通道 + 至少1个显式通道）
- 低负载（< 1K 事件/秒）：4-8
- 中等负载（1K-10K 事件/秒）：16
- 高负载（> 10K 事件/秒）：32-64
- 推荐值：CPU核心数的 2-4 倍

### Q: 如何实现事件的通配符订阅？
**A:** 注册 `WildcardEventMatcher`，然后使用通配符 Topic：
```csharp
services.AddLocalEventBus().AddWildcardMatcher();

[Subscribe(Topic = "orders/*")]
public void HandleAllOrderEvents(object e) { }
```

### Q: 异常会影响其他订阅者吗？
**A:** 不会。每个订阅者独立处理，一个订阅者抛出异常不会影响其他订阅者。异常会被拦截器捕获，或者在启用重试时进行重试。

### Q: 如何监控事件总线的运行状态？
**A:** 使用 `IEventBusDiagnostics` 接口获取诊断信息，或者通过拦截器记录日志和指标。

### Q: 支持分布式事件总线吗？
**A:** LocalEventBus 是进程内事件总线，不支持跨进程通信。如需分布式场景，请考虑 MassTransit、NServiceBus 等分布式消息框架。

### Q: 如何取消订阅？
**A:** `Subscribe` 方法返回 `IDisposable`，调用 `Dispose()` 即可取消订阅：
```csharp
var subscription = eventBus.Subscribe<MyEvent>(handler);
// ...
subscription.Dispose();  // 取消订阅
```

## 📄 许可证

MIT License - 详见 [LICENSE](LICENSE) 文件

## 📚 相关资源

- [CHANGELOG.md](CHANGELOG.md) - 版本更新日志
- [示例代码](docs/examples/) - 更多使用示例
- [NuGet Package](https://www.nuget.org/packages/LocalEventBus) - NuGet 包下载

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

贡献指南：
1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

## 📮 联系

如有问题或建议，请提交 [Issue](https://github.com/your-repo/LocalEventBus/issues)。

---

**⭐ 如果这个项目对你有帮助，请给个 Star！**
