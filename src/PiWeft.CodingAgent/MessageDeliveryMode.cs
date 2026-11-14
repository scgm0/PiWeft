namespace PiWeft.CodingAgent;

/// 跨会话消息的投递方式，对齐 pi 的 <c>deliverAs</c> 与 DSH 的 <c>delivery</c>。
public enum MessageDeliveryMode
{
	/// 运行中 steer，空闲直接起一轮。默认，延迟最低。
	Auto,

	/// 运行中注入到下一次模型请求前；空闲直接起一轮。
	Steer,

	/// 运行中等这一轮所有工具与请求结束；空闲直接起一轮。
	FollowUp,
}
