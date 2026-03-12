using System.Linq.Expressions;
using System.Reflection;
using LocalEventBus.Abstractions;

namespace LocalEventBus.Internal;

/// <summary>
/// 订阅者信息
/// </summary>
public sealed class SubscriberInfo : IEquatable<SubscriberInfo>
{
    // 编译后的高性能调用委托
    private readonly Func<object, object?, CancellationToken, ValueTask> _invoker;

    /// <summary>
    /// 事件类型
    /// </summary>
    public Type EventType { get; }

    /// <summary>
    /// 订阅者目标对象
    /// </summary>
    public object Target { get; }

    /// <summary>
    /// 方法信息
    /// </summary>
    public MethodInfo Method { get; }

    /// <summary>
    /// 处理超时时间
    /// </summary>
    public TimeSpan? Timeout { get; }

    /// <summary>
    /// 执行线程选项
    /// </summary>
    public ThreadOption ThreadOption { get; }

    /// <summary>
    /// 主题（用于基于主题的路由）
    /// </summary>
    public string? Topic { get; }

    /// <summary>
    /// 订阅通道编号
    /// 如果为 null，则不限制通道（可接收所有通道的匹配事件）
    /// </summary>
    public int? ChannelId { get; }

    /// <summary>
    /// 是否为无参方法（仅通过 Topic 触发）
    /// </summary>
    public bool IsParameterless { get; }

    /// <summary>
    /// 构造函数
    /// </summary>
    public SubscriberInfo(
        Type eventType,
        object target,
        MethodInfo method,
        TimeSpan? timeout = null,
        ThreadOption threadOption = ThreadOption.BackgroundThread,
        string? topic = null,
        int? channelId = null,
        bool isParameterless = false)
    {
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Timeout = timeout;
        ThreadOption = threadOption;
        Topic = topic;
        ChannelId = channelId;
        IsParameterless = isParameterless;

        // 编译时生成高性能委托
        _invoker = CompileInvoker(target.GetType(), method, eventType, isParameterless);
    }

    /// <summary>
    /// 调用订阅者方法（高性能）
    /// </summary>
    public ValueTask InvokeAsync(object? eventData, CancellationToken cancellationToken)
    {
        return _invoker(Target, eventData, cancellationToken);
    }

    /// <summary>
    /// 使用 Expression 树编译高性能调用委托
    /// </summary>
    private static Func<object, object?, CancellationToken, ValueTask> CompileInvoker(
        Type targetType,
        MethodInfo method,
        Type eventType,
        bool isParameterless)
    {
        // 参数定义
        var targetParam = Expression.Parameter(typeof(object), "target");
        var eventParam = Expression.Parameter(typeof(object), "eventData");
        var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        // 类型转换
        var castTarget = Expression.Convert(targetParam, targetType);

        // 构建方法调用参数
        var parameters = method.GetParameters();
        var args = new List<Expression>();

        // 无参方法：仅传递 CancellationToken（如果有）
        if (isParameterless)
        {
            foreach (var param in parameters)
            {
                if (param.ParameterType == typeof(CancellationToken))
                {
                    args.Add(ctParam);
                }
            }
        }
        else
        {
            foreach (var param in parameters)
            {
                if (param.ParameterType == typeof(CancellationToken))
                {
                    args.Add(ctParam);
                }
                else if (param.ParameterType.IsAssignableFrom(eventType) || eventType.IsAssignableFrom(param.ParameterType))
                {
                    args.Add(Expression.Convert(eventParam, param.ParameterType));
                }
                else
                {
                    throw new ArgumentException(
                        $"方法 {method.Name} 参数 {param.Name} 类型 {param.ParameterType} 与事件类型 {eventType} 不兼容");
                }
            }
        }

        // 方法调用
        var methodCall = Expression.Call(castTarget, method, args);

        // 处理返回类型
        Expression body;
        if (method.ReturnType == typeof(ValueTask))
        {
            body = methodCall;
        }
        else if (method.ReturnType == typeof(Task))
        {
            // Task 转 ValueTask: new ValueTask(task)
            var ctor = typeof(ValueTask).GetConstructor([typeof(Task)])!;
            body = Expression.New(ctor, methodCall);
        }
        else if (method.ReturnType == typeof(void))
        {
            // void 返回 default(ValueTask)
            body = Expression.Block(
                methodCall,
                Expression.Default(typeof(ValueTask))
            );
        }
        else
        {
            throw new ArgumentException(
                $"方法 {method.Name} 返回类型必须是 void, Task 或 ValueTask，当前是 {method.ReturnType}");
        }

        // 编译 Lambda
        var lambda = Expression.Lambda<Func<object, object?, CancellationToken, ValueTask>>(
            body,
            targetParam,
            eventParam,
            ctParam);

        return lambda.Compile();
    }

    #region Equality

    public bool Equals(SubscriberInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return EventType == other.EventType &&
               ReferenceEquals(Target, other.Target) &&
               Method.Equals(other.Method);
    }

    public override bool Equals(object? obj) => obj is SubscriberInfo other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(EventType, Target, Method);

    public static bool operator ==(SubscriberInfo? left, SubscriberInfo? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(SubscriberInfo? left, SubscriberInfo? right) => !(left == right);

    #endregion

    public override string ToString() => $"{Target.GetType().Name}.{Method.Name}({EventType.Name})";
}
