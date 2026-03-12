using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 默认事件总线实现
/// 支持基于 Topic 的事件路由系统
/// </summary>
public sealed partial class DefaultEventBus : IEventBus, IEventBusDiagnostics
{
    private readonly object _lifecycleGate = new();
    private readonly EventSubscriberRegistry _subscriberRegistry;
    private readonly EventChannelManager _channelManager;
    private readonly EventBusOptions _options;
    private readonly IEventTypeKeyProvider _keyProvider;
    private readonly SynchronizationContext? _uiSynchronizationContext;
    private readonly List<IEventFilter> _filters = [];
    private readonly List<IEventInterceptor> _interceptors = [];
    private readonly CancellationTokenSource _cts = new();
    private int _dispatchStarted;
    private Task[]? _dispatchTasks;
    private int _disposed;

    /// <summary>
    /// 创建默认事件总线实例
    /// </summary>
    /// <param name="options">配置选项</param>
    /// <param name="keyProvider">类型键提取器</param>
    /// <param name="matcherProvider">匹配器提供者</param>
    /// <param name="filters">过滤器集合</param>
    /// <param name="interceptors">拦截器集合</param>
    /// <param name="synchronizationContext">UI/Main 线程上下文（用于 ThreadOption.UIThread）</param>
    public DefaultEventBus(
        EventBusOptions options,
        IEventTypeKeyProvider keyProvider,
        IEventMatcherProvider matcherProvider,
        IEnumerable<IEventFilter>? filters = null,
        IEnumerable<IEventInterceptor>? interceptors = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _uiSynchronizationContext = synchronizationContext ?? SynchronizationContext.Current;

        // 使用注入的匹配器提供者创建订阅者注册表
        _subscriberRegistry = new EventSubscriberRegistry(matcherProvider);
        _channelManager = new EventChannelManager(_options);

        // 添加过滤器和拦截器
        if (filters != null)
        {
            _filters.AddRange(filters);
            _filters.Sort((a, b) => a.Order.CompareTo(b.Order));
        }

        if (interceptors != null)
        {
            _interceptors.AddRange(interceptors);
            _interceptors.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    /// <summary>
    /// 创建默认事件总线实例
    /// </summary>
    public DefaultEventBus(
        EventBusOptions? options = null,
        SynchronizationContext? synchronizationContext = null)
        : this(options ?? new EventBusOptions(),
            new DefaultEventTypeKeyProvider(),
            new DefaultEventMatcherProvider(),
            null,
            null,
            synchronizationContext)
    {
    }

    /// <summary>
    /// 启动事件分发循环
    /// </summary>
    private void EnsureDispatchLoopStarted()
    {
        if (_dispatchTasks is null)
        {
            lock (_lifecycleGate)
            {
                if (_dispatchTasks is null)
                {
                    ThrowIfDisposed();
                    Interlocked.Exchange(ref _dispatchStarted, 1);
                    // 为每个分片创建独立的处理任务
                    var shardedChannels = _channelManager.GetShardedChannels();
                    _dispatchTasks = new Task[shardedChannels.Count];

                    for (int i = 0; i < shardedChannels.Count; i++)
                    {
                        var shardIndex = i;
                        _dispatchTasks[i] = Task.Run(() => DispatchShardAsync(shardedChannels[shardIndex]), _cts.Token);
                    }
                }
            }
        }
    }

    private void MarkDispatchStarted()
    {
        lock (_lifecycleGate)
        {
            ThrowIfDisposed();
            Interlocked.Exchange(ref _dispatchStarted, 1);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(DefaultEventBus));
        }
    }
}
