using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;

namespace PiWeft.CodingAgent.Compatibility;

/// PiDeck 的模型请求快照通道（`pi-deck-model-trace` 扩展的等价实现）。
/// <para>
/// PiDeck 的 RPC 日志只覆盖 stdio，模型真正收到的请求体在进程内组装后直接走 HTTP；
/// 切到本端口后 PiDeck 的「模型」视图要能继续工作，所以这里在同一个观测点把请求体
/// 推回 PiDeck 的回环端点。
/// </para>
/// <para>
/// 线协议与扩展逐字段一致：POST <c>PIDECK_MODEL_TRACE_URL</c>，头带
/// <c>x-pideck-model-trace-token</c>；请求帧 <c>kind:"request"</c>，响应帧 <c>kind:"response"</c>。
/// 只推 payload 本体，不采集请求头。任何失败都只让这一条快照缺席。
/// </para>
public sealed class PiDeckModelTrace : IDisposable
{
	public const string UrlVariable = "PIDECK_MODEL_TRACE_URL";

	public const string TokenVariable = "PIDECK_MODEL_TRACE_TOKEN";

	/// 单条 payload 的 UTF-8 字节上限；超过按头部截断（与扩展同值）。
	public const int MaxPayloadBytes = 1_536_000;

	private const int MaxInFlight = 4;

	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

	private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(1),
		ConnectTimeout = TimeSpan.FromSeconds(2),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	private readonly string _endpoint;
	private readonly string _token;
	private readonly string _sessionId;
	private readonly ConcurrentQueue<PendingRequest> _pending = new();
	private int _inFlight;
	private int _disposed;

	private sealed record PendingRequest(string TraceId, long StartedAt);

	/// 按当前进程环境创建；没有设置环境变量时返回 <see langword="null" />（纯终端跑 pi 的情形）。
	public static PiDeckModelTrace? FromEnvironment(string sessionId)
	{
		var url = Environment.GetEnvironmentVariable(UrlVariable)?.Trim();
		var token = Environment.GetEnvironmentVariable(TokenVariable)?.Trim();
		return string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)
			? null
			: new PiDeckModelTrace(url.TrimEnd('/'), token, sessionId);
	}

	private PiDeckModelTrace(string endpoint, string token, string sessionId)
	{
		_endpoint = endpoint;
		_token = token;
		_sessionId = sessionId;
	}

	/// 请求体在发出前回调：记下 traceId 并推一条请求帧。
	public void OnRequestPayload(JsonNode payload, Model model)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			return;
		}

		try
		{
			var json = payload.ToJsonString(PiJson.NodeOptions);
			var bytes = Encoding.UTF8.GetByteCount(json);
			var truncated = bytes > MaxPayloadBytes;
			var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			var traceId = NewTraceId();
			// 先入队再发送：响应一定晚于本次回调，入队顺序就是配对顺序。
			_pending.Enqueue(new PendingRequest(traceId, startedAt));

			Post(new JsonObject
			{
				["kind"] = "request",
				["traceId"] = traceId,
				["ts"] = startedAt,
				["model"] = model.Id,
				["provider"] = model.Provider,
				["sessionId"] = _sessionId,
				["payloadJson"] = truncated ? TruncateJsonHead(json, MaxPayloadBytes) : json,
				["payloadBytes"] = bytes,
				["truncated"] = truncated,
				["messageCount"] = CountArray(payload, ["messages", "input", "contents"]),
				["toolCount"] = CountArray(payload, ["tools", "functions"]),
			});
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
		{
			// 循环引用等极端 payload：这一条缺席，会话照常。
		}
	}

	/// 响应头到达：按到达顺序配对最早的那条请求。
	public void OnResponse(ProviderResponse response)
	{
		if (Volatile.Read(ref _disposed) != 0 || !_pending.TryDequeue(out var pending))
		{
			return;
		}

		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		Post(new JsonObject
		{
			["kind"] = "response",
			["traceId"] = pending.TraceId,
			["ts"] = now,
			["status"] = response.Status,
			["durationMs"] = Math.Max(0, now - pending.StartedAt),
		});
	}

	/// 把请求/响应回调挂到一份流选项上；已经挂过的不覆盖。
	public StreamOptions Attach(StreamOptions options) => options with
	{
		OnProviderRequestPayload = options.OnProviderRequestPayload ?? OnRequestPayload,
		OnResponse = options.OnResponse ?? ((response, _) => OnResponse(response)),
	};

	public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

	/// 生成 traceId：时间戳 36 进制 + 随机后缀。形态要匹配 PiDeck 的白名单（会进文件名）。
	internal static string NewTraceId() =>
		$"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x")}-{Guid.NewGuid().ToString("N")[..8]}";

	/// 按 UTF-8 字节保留 JSON 前缀。先按字符切（字符数不超过上限时字节数最多 3 倍，
	/// 不必为几十 MB 的图片 payload 分配等长缓冲），再回退到完整字符边界：
	/// 截断点落在多字节字符中间会在尾部留下半个序列，读回来就是 U+FFFD。
	internal static string TruncateJsonHead(string json, int maxBytes)
	{
		if (json.Length <= maxBytes)
		{
			return json;
		}

		var head = Encoding.UTF8.GetBytes(json[..maxBytes]);

		var limit = maxBytes;
		while (limit > 0)
		{
			var last = head[limit - 1];
			if ((last & 0xC0) == 0x80)
			{
				// 续字节：继续往前退到它的首字节。
				limit--;
				continue;
			}

			var length = last < 0x80 ? 1 : last >= 0xF0 ? 4 : last >= 0xE0 ? 3 : last >= 0xC0 ? 2 : 0;
			if (length > 0 && limit - 1 + length <= maxBytes)
			{
				break;
			}

			limit--;
		}

		return Encoding.UTF8.GetString(head, 0, limit);
	}

	private static int? CountArray(JsonNode payload, string[] keys)
	{
		if (payload is not JsonObject record)
		{
			return null;
		}

		foreach (var key in keys)
		{
			if (record[key] is JsonArray array)
			{
				return array.Count;
			}
		}

		return null;
	}

	/// 发一帧（fire-and-forget）。积压超过上限直接丢弃：诊断数据不值得拖慢会话。
	private void Post(JsonObject frame)
	{
		if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
		{
			Interlocked.Decrement(ref _inFlight);
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
				{
					Content = new ByteArrayContent(Encoding.UTF8.GetBytes(frame.ToJsonString(PiJson.NodeOptions))),
				};
				request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
				request.Headers.TryAddWithoutValidation("x-pideck-model-trace-token", _token);

				using var timeout = new CancellationTokenSource(RequestTimeout);
				using var response = await SharedClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException or UriFormatException or InvalidOperationException)
			{
				// PiDeck 不在（应用已退出等）：静默，不重试。
			}
			finally
			{
				Interlocked.Decrement(ref _inFlight);
			}
		});
	}
}
