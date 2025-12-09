using LocalEventBus.Abstractions;
using LocalEventBus.Internal;
using LocalEventBus.Matchers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LocalEventBus;

/// <summary>
/// 依赖注入扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加 LocalEventBus 服务
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configure">配置选项</param>
    /// <returns>EventBus 构建器</returns>
    public static IEventBusBuilder AddLocalEventBus(
        this IServiceCollection services,
        Action<EventBusOptions>? configure = null)
    {
        var options = new EventBusOptions();
        configure?.Invoke(options);

        // 注册配置
        services.AddSingleton(options);

        services.TryAddSingleton<IEventTypeKeyProvider, DefaultEventTypeKeyProvider>();

        // 注册匹配器提供者
        services.TryAddSingleton<IEventMatcherProvider, DefaultEventMatcherProvider>();

        // 注册订阅者注册表
        services.TryAddSingleton<EventSubscriberRegistry>();

        // 注册事件总线
        services.TryAddSingleton<IEventBus, DefaultEventBus>();
        services.TryAddSingleton<IEventPublisher>(sp => sp.GetRequiredService<IEventBus>());
        services.TryAddSingleton<IEventSubscriber>(sp => sp.GetRequiredService<IEventBus>());
        services.TryAddSingleton<IEventBusDiagnostics>(sp => (IEventBusDiagnostics)sp.GetRequiredService<IEventBus>());

        return new EventBusBuilder(services, options);
    }

    /// <summary>
    /// 添加 LocalEventBus 服务（使用配置节）
    /// </summary>
    public static IEventBusBuilder AddLocalEventBus(
        this IServiceCollection services,
        EventBusOptions options)
    {
        return services.AddLocalEventBus(o =>
        {
            o.ChannelCapacity = options.ChannelCapacity;
            o.ChannelFullMode = options.ChannelFullMode;
            o.DefaultTimeout = options.DefaultTimeout;
            o.PartitionCount = options.PartitionCount;
            o.RetryOptions = options.RetryOptions;
        });
    }
}

/// <summary>
/// EventBus 构建器接口
/// </summary>
public interface IEventBusBuilder
{
    /// <summary>
    /// 服务集合
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// 配置选项
    /// </summary>
    EventBusOptions Options { get; }

    /// <summary>
    /// 添加事件匹配器
    /// </summary>
    IEventBusBuilder AddMatcher<TMatcher>() where TMatcher : class, IEventMatcher;

    /// <summary>
    /// 添加通配符匹配器
    /// </summary>
    IEventBusBuilder AddWildcardMatcher();

    /// <summary>
    /// 添加正则表达式匹配器
    /// </summary>
    IEventBusBuilder AddRegexMatcher();

    /// <summary>
    /// 添加事件过滤器
    /// </summary>
    IEventBusBuilder AddFilter<TFilter>() where TFilter : class, IEventFilter;

    /// <summary>
    /// 添加事件拦截器
    /// </summary>
    IEventBusBuilder AddInterceptor<TInterceptor>() where TInterceptor : class, IEventInterceptor;
}

/// <summary>
/// EventBus 构建器实现
/// </summary>
internal sealed class EventBusBuilder : IEventBusBuilder
{
    public IServiceCollection Services { get; }
    public EventBusOptions Options { get; }

    public EventBusBuilder(IServiceCollection services, EventBusOptions options)
    {
        Services = services;
        Options = options;
    }

    public IEventBusBuilder AddMatcher<TMatcher>() where TMatcher : class, IEventMatcher
    {
        Services.AddTransient<IEventMatcher, TMatcher>();
        return this;
    }


    public IEventBusBuilder AddWildcardMatcher()
    {
        Services.AddTransient<IEventMatcher, WildcardEventMatcher>();
        return this;
    }

    public IEventBusBuilder AddRegexMatcher()
    {
        Services.AddTransient<IEventMatcher, RegexEventMatcher>();
        return this;
    }

    public IEventBusBuilder AddFilter<TFilter>() where TFilter : class, IEventFilter
    {
        Services.AddTransient<IEventFilter, TFilter>();
        return this;
    }

    public IEventBusBuilder AddInterceptor<TInterceptor>() where TInterceptor : class, IEventInterceptor
    {
        Services.AddTransient<IEventInterceptor, TInterceptor>();
        return this;
    }
}
