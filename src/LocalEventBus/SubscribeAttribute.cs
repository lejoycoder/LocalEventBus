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
    /// 超时时间（毫秒），0 表示使用默认值
    /// </summary>
    public double Timeout { get; set; } = 0;

    /// <summary>
    /// 订阅者执行线程选项（默认 BackgroundThread）
    /// </summary>
    public ThreadOption ThreadOption { get; set; } = ThreadOption.BackgroundThread;

    /// <summary>
    /// 订阅通道编号
    /// 默认 -1，表示不限制通道（可接收所有通道的匹配事件）
    /// </summary>
    public int ChannelId { get; set; } = -1;

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
