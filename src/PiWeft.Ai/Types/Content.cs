using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PiWeft.Ai.Json;

namespace PiWeft.Ai;

/// 助手或用户消息中的一个内容块，对应 <c>TextContent | ThinkingContent | ImageContent | ToolCall</c>
/// （来自 <c>@earendil-works/pi-ai</c>）。序列化时带显式的 <c>type</c> 判别字段。
/// <c>closed</c> 把可行类固定在程序集内，处理内容块的 switch 由编译器验证是否兑尽所有情形。
[JsonConverter(typeof(ContentBlockJsonConverter))]
public closed record ContentBlock
{
	public abstract string Type { get; }
}

/// 正文块。超过 <see cref="DeferredTextThreshold"/> 的正文只留 UTF-8 字节，<see cref="Text"/> 访问时再解码。
/// 工具输出常在几十 KB 量级，常驻 UTF-16 是大会话托管堆占用的主要来源（约为文件的 2×）。
public sealed record TextContent : ContentBlock
{
	/// 正文超过这个字符数就改存 UTF-8。8 KiB 以下的正文解码开销和字符串本身都不值得延迟。
	public const int DeferredTextThreshold = 8192;

	private byte[]? _utf8;
	private int _length;
	private string? _text;
	// 流式来源：provider 每个 delta 只 append 进 StringBuilder，快照共享同一个实例。
	// 没有它就变成每个 delta 一次 ToString()，长回复的分配是 O(n²)。
	private StringBuilder? _streaming;
	private string? _streamingText;

	public override string Type => "text";

	/// 正文。延迟存储的正文每次访问都重新解码，不做常驻缓存：一旦缓存，第一次读就把省下的
	/// UTF-16 又常驻回来了。反复读同一段大正文的调用方应当自己持有结果。
	/// 流式来源不同：它只在读取时物化一次并缓存，否则最终消息被渲染、写盘、导出会各解一遍。
	public string Text
	{
		get
		{
			if (_streaming is { } streaming)
			{
				return _streamingText ??= streaming.ToString();
			}

			return _text ?? (_utf8 is null ? string.Empty : Encoding.UTF8.GetString(_utf8));
		}
		init
		{
			_text = value ?? string.Empty;
			_utf8 = null;
			_streaming = null;
			_streamingText = null;
			_length = _text.Length;
		}
	}

	/// 正文字符数。延迟存储时不需要解码。
	public int TextLength => _streaming?.Length ?? _length;

	/// 流式正文：delta 追加进 <paramref name="buffer"/>，本实例共享它，读取 <see cref="Text"/> 时才物化。
	internal static TextContent FromStreaming(StringBuilder buffer) => new() { _streaming = buffer };

	/// 是否还挂着流式缓冲。
	internal bool IsStreaming => _streaming is not null;

	/// 落成普通块：物化正文并松开 StringBuilder，最终消息不再按三倍占内存。
	internal TextContent Materialize() => _streaming is null ? this : this with { Text = Text };

	/// 正文前 <paramref name="maxCharacters"/> 个字符。延迟存储时只解码覆盖这段的字节：
	/// 摘要、单行预览这类有界消费方不该为一两个前缀字符把几十 KB 的正文整篇解出来。
	public string Prefix(int maxCharacters)
	{
		if (maxCharacters <= 0 || TextLength == 0)
		{
			return string.Empty;
		}

		if (_streaming is { } streaming)
		{
			// 直接取 buffer，不走 Text 的缓存：缓存只服务整篇读取，前缀不该依赖它是否已经物化。
			return streaming.Length <= maxCharacters
				? streaming.ToString()
				: streaming.ToString(0, maxCharacters);
		}

		if (_text is not null)
		{
			return _text.Length <= maxCharacters ? _text : _text[..maxCharacters];
		}

		if (_utf8 is null)
		{
			return string.Empty;
		}

		if (maxCharacters >= _length)
		{
			return Text;
		}

		// 一个字符最多占 4 字节；多取 4 字节让被截断的字符落在取用范围之外。
		var byteCount = (int)Math.Min(_utf8.Length, ((long)maxCharacters * 4) + 4);
		var buffer = new char[byteCount];
		var decoded = Encoding.UTF8.GetChars(_utf8.AsSpan(0, byteCount), buffer);
		return new string(buffer, 0, Math.Min(decoded, maxCharacters));
	}

	/// 正文是否只存在 UTF-8 里（调试、测试与诊断用）。
	public bool IsTextDeferred => _utf8 is not null;

	/// 延迟存储的 UTF-8 正文。写回会话时直接写这份字节，不再解码成 UTF-16。
	internal bool TryGetUtf8(out ReadOnlySpan<byte> utf8)
	{
		utf8 = _utf8;
		return _utf8 is not null;
	}

	public string? TextSignature { get; init; }

	public static TextContent Of(string text) => new() { Text = text };

	/// 直接用 UTF-8 正文建块。<paramref name="characterLength"/> 是解码后的字符数，解码前必须已知。
	/// 传入的是 span，内部会复制一份。
	public static TextContent FromUtf8(ReadOnlySpan<byte> utf8, int characterLength) =>
		FromOwnedUtf8(utf8.ToArray(), characterLength);

	/// 接管 <paramref name="utf8"/> 所有权，不再复制：解析路径上这份数组刚分配好，
	/// 再拷一遍会白白多出与正文等量的一次分配（600 条工具输出就是几十 MB）。
	internal static TextContent FromOwnedUtf8(byte[] utf8, int characterLength) => new()
	{
		_utf8 = utf8,
		_length = characterLength,
	};

	public bool Equals(TextContent? other) =>
		other is not null && TextSignature == other.TextSignature && string.Equals(Text, other.Text, StringComparison.Ordinal);

	// 相等性按正文语义：两份内容相同的块不能因为「一份在字符串里、一份在 UTF-8 里」而不等。
	// 代价是散列会解码大正文，所以不要把大块正文放进字典/集合的键里。
	public override int GetHashCode() => HashCode.Combine(Text, TextSignature);
}

public sealed record ThinkingContent : ContentBlock
{
	private string _thinking = string.Empty;
	// 流式来源：provider 每个 delta 只 append 进 StringBuilder，快照共享同一个实例。
	private StringBuilder? _streaming;
	private string? _streamingText;

	public override string Type => "thinking";

	/// 思考正文。流式块在第一次读取时物化并缓存；中间快照没人读它，所以流式期间不分配。
	public string Thinking
	{
		get => _streaming is { } streaming ? _streamingText ??= streaming.ToString() : _thinking;
		init
		{
			_thinking = value ?? string.Empty;
			_streaming = null;
			_streamingText = null;
		}
	}

	/// 思考正文字符数。流式来源不需要物化。
	public int ThinkingLength => _streaming?.Length ?? _thinking.Length;

	/// 流式思考：delta 追加进 <paramref name="buffer"/>，本实例共享它。
	internal static ThinkingContent FromStreaming(StringBuilder buffer, string? signature = null) =>
		new() { _streaming = buffer, ThinkingSignature = signature };

	/// 是否还挂着流式缓冲。
	internal bool IsStreaming => _streaming is not null;

	/// 落成普通块：物化正文并松开 StringBuilder。
	internal ThinkingContent Materialize() => _streaming is null ? this : this with { Thinking = Thinking };

	public string? ThinkingSignature { get; init; }

	public bool? Redacted { get; init; }

	// record 自动生成的相等性会比较 _streaming 引用：两个内容相同的块不能因为来源不同而不等。
	public bool Equals(ThinkingContent? other) =>
		other is not null &&
		string.Equals(Thinking, other.Thinking, StringComparison.Ordinal) &&
		string.Equals(ThinkingSignature, other.ThinkingSignature, StringComparison.Ordinal) &&
		Redacted == other.Redacted;

	public override int GetHashCode() => HashCode.Combine(Thinking, ThinkingSignature, Redacted);
}

/// 把流式块落成普通块：最终消息只留正文，不再同时持有 StringBuilder 与物化后的字符串。
internal static class ContentBlocks
{
	public static List<ContentBlock> Materialize(IReadOnlyList<ContentBlock> blocks)
	{
		var result = new List<ContentBlock>(blocks.Count);
		foreach (var block in blocks)
		{
			result.Add(block switch
			{
				TextContent { IsStreaming: true } text => text.Materialize(),
				ThinkingContent { IsStreaming: true } thinking => thinking.Materialize(),
				_ => block,
			});
		}

		return result;
	}
}

public sealed record ImageContent : ContentBlock
{
	public override string Type => "image";

	public string Data { get; init; } = string.Empty;

	public string MimeType { get; init; } = string.Empty;
}

public sealed record ToolCall : ContentBlock
{
	public override string Type => "toolCall";

	public string Id { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public JsonObject Arguments { get; init; } = new();

	/// 模型给出的完整参数不是合法 JSON 对象时为错误文本，<see cref="Arguments"/> 则为空对象。
	/// 工具参数校验会用它失败并把原文反馈给模型。
	public string? ArgumentsError { get; init; }

	public string? ThoughtSignature { get; init; }

	[JsonPropertyName("namespace")]
	public string? Namespace { get; init; }
}

public sealed record TextSignatureV1
{
	public int V { get; init; } = 1;

	public string Id { get; init; } = string.Empty;

	public string? Phase { get; init; }
}
