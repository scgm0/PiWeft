namespace PiWeft.Ai;

/// <see cref="AssistantMessageEventStream"/> 的事件协议。
/// 成功的流会先发 <see cref="StartEvent"/>，随后是若干增量更新，最后以
/// <see cref="DoneEvent"/> 终止。若请求尚未开始生成就失败，流可以直接以 <see cref="ErrorEvent"/> 终止；
/// 而在 <c>start</c> 之后发生的失败同样以 <c>error</c> 终止。
/// 文本与思考块在各自的 <c>*_start</c> 事件时还是空的，只通过 <c>*_delta</c> 事件增长。
/// <c>closed</c>：事件种类固定在程序集内，消费方的 switch 由编译器验证兑尽。
public closed record AssistantMessageEvent
{
	public required AssistantMessage Partial { get; init; }
}

public sealed record StartEvent : AssistantMessageEvent;

public sealed record TextStartEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }
}

public sealed record TextDeltaEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required string Delta { get; init; }
}

public sealed record TextEndEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required string Content { get; init; }
}

public sealed record ThinkingStartEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }
}

public sealed record ThinkingDeltaEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required string Delta { get; init; }
}

public sealed record ThinkingEndEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required string Content { get; init; }
}

public sealed record ToolCallStartEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }
}

public sealed record ToolCallDeltaEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required string Delta { get; init; }
}

public sealed record ToolCallEndEvent : AssistantMessageEvent
{
	public required int ContentIndex { get; init; }

	public required ToolCall ToolCall { get; init; }
}

public sealed record DoneEvent : AssistantMessageEvent
{
	public required StopReason Reason { get; init; }

	public AssistantMessage Message => Partial;
}

public sealed record ErrorEvent : AssistantMessageEvent
{
	public required StopReason Reason { get; init; }

	public AssistantMessage Error => Partial;
}
