using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Ai.Providers.Anthropic;

/// 会话级 Anthropic message thread 状态。服务器持有 thread 里的对话，
/// 后续请求只发新增消息；前缀变了就退回完整请求并开新 thread。
/// <para>
/// 哈希与续接请求体都用 <see cref="Utf8JsonWriter"/> 直接写进缓冲再算哈希，不复制消息树：
/// 每个请求都要过一遍整个上下文，深拷贝一份会白白分配几百 KB。
/// </para>
internal static class AnthropicThreads
{
	private const int MaxSessions = 10_000;

	private static readonly string[] SettingsKeys =
		["model", "system", "tools", "thinking", "output_config", "tool_choice"];

	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = false,
		SkipValidation = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// 上一次请求的消息数 + 已转换的回复、前缀哈希、可续接的回复 id。
	private sealed record ThreadEntry(int PrefixCount, string PrefixHash, string ReplyId);

	public enum RequestKind
	{
		Full,
		Create,
		Continue,
	}

	private static readonly Dictionary<string, object> State = new(StringComparer.Ordinal);
	private static readonly Lock Gate = new();

	/// 服务器拒绝续接时返回 <c>thread_not_found</c> 等错误码，重发一次新 thread 即可恢复。
	public static bool StartsNewThread(string? code) => code is
		"thread_not_found" or "thread_already_continued" or "thread_continue_in_flight" or "thread_fingerprint_mismatch";

	/// 端点不认识 <c>thread</c> 字段：关掉本会话的 thread，之后按普通请求发送。
	public static bool ThreadsUnsupported(string? code, int status, string message) =>
		code == "thread_unsupported_request" ||
		(code is null && status == 400 && message.Contains("thread", StringComparison.OrdinalIgnoreCase));

	/// 默认只为直连 Anthropic 打开；网关要显式声明 <c>compat.supportsMessageThreads</c>。
	public static bool Enabled(Model model) =>
		model.Compat?["supportsMessageThreads"] is JsonValue flag && flag.TryGetValue<bool>(out var declared)
			? declared
			: string.Equals(model.Provider, "anthropic", StringComparison.Ordinal) &&
				model.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

	public static bool IsOff(string sessionId)
	{
		lock (Gate)
		{
			return State.TryGetValue(sessionId, out var value) && value is string { Length: 0 };
		}
	}

	public static void TurnOff(string sessionId) => Set(sessionId, string.Empty);

	public static void Reset(string sessionId)
	{
		lock (Gate)
		{
			State.Remove(sessionId);
		}
	}

	/// 这次请求能不能续接。能续接就取走状态（每条回复只能续接一次，并发请求不会重复续接）。
	public static RequestKind Decide(string sessionId, JsonObject body, out int prefixCount, out string replyId)
	{
		prefixCount = 0;
		replyId = string.Empty;
		ThreadEntry? entry;
		lock (Gate)
		{
			if (!State.TryGetValue(sessionId, out var raw) || raw is not ThreadEntry stored)
			{
				return RequestKind.Create;
			}

			entry = stored;
		}

		if (body["messages"] is JsonArray messages && messages.Count > entry.PrefixCount &&
			string.Equals(entry.PrefixHash, HashPrefix(body, entry.PrefixCount), StringComparison.Ordinal))
		{
			lock (Gate)
			{
				State.Remove(sessionId);
			}

			prefixCount = entry.PrefixCount;
			replyId = entry.ReplyId;
			return RequestKind.Continue;
		}

		return RequestKind.Create;
	}

	/// 按续接方式写请求体：<c>continue</c> 去掉 <c>system</c>/<c>tools</c> 并只带新增消息，
	/// <c>create</c> 原样加 <c>thread</c>，<c>full</c> 原样输出。
	public static byte[] WriteBody(JsonObject body, RequestKind kind, int prefixCount, string replyId)
	{
		var buffer = new ArrayBufferWriter<byte>(4096);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			writer.WriteStartObject();
			var messages = body["messages"] as JsonArray;
			foreach (var (key, value) in body)
			{
				if (key is "thread")
				{
					continue;
				}

				if (kind == RequestKind.Continue)
				{
					if (key is "system" or "tools" or "messages")
					{
						continue;
					}
				}
				else if (key == "messages")
				{
					continue;
				}

				writer.WritePropertyName(key);
				if (value is null)
				{
					writer.WriteNullValue();
				}
				else
				{
					value.WriteTo(writer);
				}
			}

			if (messages is not null)
			{
				writer.WritePropertyName("messages");
				writer.WriteStartArray();
				var start = kind == RequestKind.Continue ? Math.Min(prefixCount, messages.Count) : 0;
				for (var index = start; index < messages.Count; index++)
				{
					// 原样发送：规范化只用于哈希，改写消息形状会改变模型看到的内容。
					if (messages[index] is { } message)
					{
						message.WriteTo(writer);
					}
					else
					{
						writer.WriteNullValue();
					}
				}

				writer.WriteEndArray();
			}

			if (kind != RequestKind.Full)
			{
				writer.WritePropertyName("thread");
				writer.WriteStartObject();
				writer.WriteString("type", kind == RequestKind.Continue ? "continue" : "create");
				if (kind == RequestKind.Continue)
				{
					writer.WriteString("previous_message_id", replyId);
				}

				writer.WriteEndObject();
			}

			writer.WriteEndObject();
		}

		return buffer.WrittenSpan.ToArray();
	}

	/// 记录下一次请求可以续接的位置。<paramref name="replyId"/> 为空时不记录（例如没有 message_start）。
	/// fallback 回复（responseModel 与请求模型不同）服务器不保存，不能续接。
	public static void Record(string sessionId, JsonObject body, JsonArray reply, string replyId)
	{
		if (replyId.Length == 0 || body["messages"] is not JsonArray messages)
		{
			return;
		}

		var prefixCount = messages.Count + reply.Count;
		Set(sessionId, new ThreadEntry(prefixCount, HashPrefix(body, messages.Count, reply), replyId));
	}

	/// 哈希覆盖「必须在线程内保持一致」的部分：端点、模型、system、tools、thinking、output_config 等，
	/// 以及已经发过的消息。cache_control 标记不改变语义但要移动，哈希时剥掉；
	/// 最新用户消息在上一请求里是 text 块、下一请求里是字符串，统一成块形式。
	internal static string HashPrefix(JsonObject body, int messageCount, JsonArray? reply = null)
	{
		var buffer = new ArrayBufferWriter<byte>(4096);
		using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
		{
			writer.WriteStartObject();
			foreach (var key in SettingsKeys)
			{
				writer.WritePropertyName(key);
				WriteNode(writer, body[key]);
			}

			writer.WritePropertyName("messages");
			writer.WriteStartArray();
			if (body["messages"] is JsonArray messages)
			{
				for (var index = 0; index < messageCount && index < messages.Count; index++)
				{
					WriteNode(writer, messages[index]);
				}
			}

			if (reply is not null)
			{
				foreach (var item in reply)
				{
					WriteNode(writer, item);
				}
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan))[..16];
	}

	/// 规范化写出：丢掉 <c>cache_control</c>，把字符串 <c>content</c> 展开成 text 块。
	private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
	{
		switch (node)
		{
			case null:
				writer.WriteNullValue();
				break;
			case JsonObject obj:
				writer.WriteStartObject();
				foreach (var (key, value) in obj)
				{
					if (key == "cache_control")
					{
						continue;
					}

					writer.WritePropertyName(key);
					if (key == "content" && value is JsonValue scalar &&
						scalar.TryGetValue<string>(out var text))
					{
						writer.WriteStartArray();
						writer.WriteStartObject();
						writer.WriteString("type", "text");
						writer.WriteString("text", text);
						writer.WriteEndObject();
						writer.WriteEndArray();
						continue;
					}

					WriteNode(writer, value);
				}

				writer.WriteEndObject();
				break;
			case JsonArray array:
				writer.WriteStartArray();
				foreach (var item in array)
				{
					WriteNode(writer, item);
				}

				writer.WriteEndArray();
				break;
			default:
				node.WriteTo(writer);
				break;
		}
	}

	private static void Set(string sessionId, object value)
	{
		lock (Gate)
		{
			State.Remove(sessionId);
			State[sessionId] = value;
			if (State.Count > MaxSessions)
			{
				// 最旧的会话先丢：它的 thread 在服务器上大概率也已经过期。
				using var enumerator = State.Keys.GetEnumerator();
				if (enumerator.MoveNext() && enumerator.Current is { } oldest)
				{
					State.Remove(oldest);
				}
			}
		}
	}
}
