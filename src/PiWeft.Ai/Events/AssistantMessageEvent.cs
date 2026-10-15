namespace PiWeft.Ai;

/// <summary>
/// <see cref="AssistantMessageEventStream"/> 的事件协议。
///
/// 成功的流会先发 <see cref="StartEvent"/>，随后是若干增量更新，最后以
/// <see cref="DoneEvent"/> 终止。若请求尚未开始生成就失败，流可以直接以 <see cref="ErrorEvent"/> 终止；
/// 而在 <c>start</c> 之后发生的失败同样以 <c>error</c> 终止。
/// 文本与思考块在各自的 <c>*_start</c> 事件时还是空的，只通过 <c>*_delta</c> 事件增长。
/// </summary>
public abstract record AssistantMessageEvent
{
	/// <summary>到目前为止的实时响应。它是共享实例，不是事件时刻的快照。</summary>
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

/// <summary>成功终止事件。<paramref name="Reason"/> 取 stop、length、toolUse、deferred 之一。</summary>
public sealed record DoneEvent : AssistantMessageEvent
{
	public required StopReason Reason { get; init; }

	/// <summary>最终消息，与 <see cref="AssistantMessageEvent.Partial"/> 是同一实例。</summary>
	public AssistantMessage Message => Partial;
}

/// <summary>失败终止事件。<paramref name="Reason"/> 取 aborted 或 error。</summary>
public sealed record ErrorEvent : AssistantMessageEvent
{
	public required StopReason Reason { get; init; }

	/// <summary>失败的消息，与 <see cref="AssistantMessageEvent.Partial"/> 是同一实例。</summary>
	public AssistantMessage Error => Partial;
}
