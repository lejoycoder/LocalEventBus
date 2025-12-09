namespace LocalEventBus.Tests.Events;

/// <summary>
/// 测试用订单创建事件
/// </summary>
public record OrderCreatedEvent(int OrderId, string CustomerName, decimal Amount);

/// <summary>
/// 测试用支付完成事件
/// </summary>
public record PaymentCompletedEvent(int OrderId, decimal Amount, DateTime PaidAt);

/// <summary>
/// 测试用通用消息事件
/// </summary>
public record MessageEvent(string Message);
