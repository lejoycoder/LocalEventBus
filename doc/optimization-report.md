# LocalEventBus 项目代码优化报告

## 1. 项目分析结论

LocalEventBus 是一个基于 `Channel` 的进程内事件总线，核心链路清晰：

1. 发布路径：`Publish/PublishAsync` -> 通道入队 -> 分片消费 -> 订阅者执行。
2. 直调路径：`InvokeAsync/InvokeByTopicAsync`，不经过队列，强调即时调用。
3. 扩展能力：支持 Filter、Interceptor、Topic 匹配器、显式通道路由。

代码整体设计良好，但在高频路径和生命周期边界上存在可优化空间。

## 2. 已实施优化

### 2.1 生命周期安全增强（Dispose 后快速失败）

问题：公开 API 在对象释放后仍可能进入逻辑分支，容易出现不稳定行为。

优化：在关键公开方法统一增加 `ThrowIfDisposed()`。

覆盖方法：

1. `Publish/PublishAsync`
2. `PublishByTopic/PublishByTopicAsync`
3. `Subscribe` 系列
4. `Unsubscribe`
5. `InvokeAsync/InvokeByTopicAsync`
6. `AddFilter/AddInterceptor`

收益：避免已释放对象继续参与分发/订阅流程，错误语义更明确。

涉及文件：`src/LocalEventBus/Internal/DefaultEventBus.cs`

### 2.2 分发启动与管线变更并发保护

问题：原实现使用 `lock(this)`，且过滤器/拦截器添加与分发启动之间存在竞态窗口。

优化：

1. 新增 `_lifecycleGate` 作为专用锁，替换 `lock(this)`。
2. 新增 `MarkDispatchStarted()`，统一用锁保护“标记分发已启动”的时机。
3. `AddFilter/AddInterceptor` 在同一锁内校验 `_dispatchStarted` 并修改集合。

收益：减少并发竞态风险，避免外部锁住实例导致潜在死锁风险。

涉及文件：`src/LocalEventBus/Internal/DefaultEventBus.cs`

### 2.3 分发热点路径去掉 LINQ 分配

问题：`DispatchEventAsync` 在每次事件分发时使用 `Where/ToArray/Select`，会产生额外分配。

优化：

1. 改为单次遍历 + 路由判断（`MatchesRouteAudience`）。
2. `0/1/N` 任务分支优化，降低小规模订阅场景分配。

收益：减少热路径临时对象创建，提升吞吐稳定性。

涉及文件：`src/LocalEventBus/Internal/DefaultEventBus.cs`

### 2.4 未指定通道路由去除临时 List 分配

问题：`ResolveUnspecifiedRouteChannelId()` 原先每次创建 `List<int>` 后再选最小负载。

优化：

1. 改为单次扫描通道，直接做最小负载选择。
2. 并列负载继续使用蓄水池采样随机打散。
3. `EventChannelManager` 增加 `GetChannelLoad(int)` 支持读取负载。

收益：降低发布路径分配，保持路由语义不变。

涉及文件：

1. `src/LocalEventBus/Internal/DefaultEventBus.cs`
2. `src/LocalEventBus/Internal/EventChannelManager.cs`

### 2.5 反射扫描重复开销优化

问题：订阅者扫描时对同一方法重复读取 `SubscribeAttribute`。

优化：扫描阶段一次读取并缓存 `(Method, Attributes)`，复用到后续构建。

收益：降低订阅初始化反射开销，提升注册阶段效率。

涉及文件：`src/LocalEventBus/Internal/DefaultEventBus.cs`

### 2.6 异步释放补齐分发任务等待

问题：`DisposeAsync()` 之前只取消并释放通道，未等待已启动分发任务完成。

优化：在 `DisposeAsync()` 中等待 `_dispatchTasks` 结束（取消视为预期）。

收益：异步释放语义更完整，减少后台任务悬挂风险。

涉及文件：`src/LocalEventBus/Internal/DefaultEventBus.cs`

## 3. 测试与验证

新增测试文件：

1. `tests/LocalEventBus.Tests/DisposalSafetyTests.cs`

新增测试点：

1. `PublishAsync` 在 `DisposeAsync` 后抛出 `ObjectDisposedException`
2. `Subscribe` 在 `Dispose` 后抛出 `ObjectDisposedException`
3. `AddFilter` 在 `Dispose` 后抛出 `ObjectDisposedException`

执行命令：

```powershell
$env:DOTNET_CLI_HOME = (Join-Path (Get-Location) '.dotnet_home')
dotnet test tests/LocalEventBus.Tests/LocalEventBus.Tests.csproj -p:RestoreSources=https://api.nuget.org/v3/index.json
```

结果：

1. 总计 50 个测试
2. 通过 50，失败 0

## 4. 后续建议（可选）

1. 处理 `CS8714` 空引用约束警告，统一 `eventData` 在 Filter/Interceptor 的空值策略。
2. 清理或修正环境中的无效 NuGet 源（当前存在 `https://example.com/nuget/v3/index.json`）。
3. 为热点路径增加 BenchmarkDotNet 对比基线，量化本次优化收益。
