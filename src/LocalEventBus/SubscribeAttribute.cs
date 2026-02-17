using LocalEventBus.Abstractions;

namespace LocalEventBus;

/// <summary>
/// 标记方法为事件订阅处理器
/// 支持一个方法添加多个 [Subscribe] 特性来订阅多个 Topic
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class SubscribeAttribute : Attribute
{
    /// <summary>
    /// 订阅的主题
    /// 如果为 null 或空，则订阅方法参数类型的全名（Type.FullName）
    /// </summary>
    public string? Topic { get; set; }

    /// <summary>
    /// 优先级 (0-10，数字越大优先级越高，默认 5)
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// 是否允许并发处理
    /// </summary>
    public bool AllowConcurrency { get; set; } = true;

    /// <summary>
    /// 超时时间（毫秒），0 表示使用默认值
    /// </summary>
    public double Timeout { get; set; } = 0;

    /// <summary>
    /// 订阅者执行线程选项（默认 BackgroundThread）
    /// </summary>
    public ThreadOption ThreadOption { get; set; } = ThreadOption.BackgroundThread;

    /// <summary>
    /// 创建订阅特性
    /// </summary>
    public SubscribeAttribute()
    {
    }

    /// <summary>
    /// 创建订阅特性并指定主题
    /// </summary>
    /// <param name="topic">订阅的主题</param>
    public SubscribeAttribute(string topic)
    {
        Topic = topic;
    }
}
