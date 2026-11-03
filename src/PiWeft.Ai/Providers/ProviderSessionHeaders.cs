using System.Net.Http;

namespace PiWeft.Ai.Providers;

/// <summary>
/// 会话亲和请求头：pi 的模型 compat 允许网关按会话粘性路由/缓存。
/// 缺失时通常不会 400（OpenCode 的 session 头是特例），但会把同一会话的请求打散到不同副本，
/// 表现为缓存不命中、首 token 变慢。规则与 pi-ai 的 createClient 一致：
/// - openrouter 格式：<c>x-session-id</c>；
/// - openai 格式：<c>session_id</c> + <c>x-client-request-id</c>（completions 另加 <c>x-session-affinity</c>）；
/// - <c>openai-nosession</c>：只发后两者，不发 <c>session_id</c>。
/// 显式配置的同名头优先，空值也算显式覆盖。
/// </summary>
public static class ProviderSessionHeaders
{
	/// <summary>OpenAI-compatible completions 的亲和头。</summary>
	public static void ApplyCompletions(Model model, StreamOptions options, HttpRequestMessage request)
	{
		if (string.IsNullOrWhiteSpace(options.SessionId)) return;
		if (!Enabled(model, IsOpenRouter(model))) return;

		if (Format(model) == "openrouter")
		{
			Add(request, "x-session-id", options.SessionId);
			return;
		}

		if (Format(model) == "openai") Add(request, "session_id", options.SessionId);
		Add(request, "x-client-request-id", options.SessionId);
		Add(request, "x-session-affinity", options.SessionId);
	}

	/// <summary>OpenAI Responses 的亲和头（pi-ai 在 Responses 路径不检查 sendSessionAffinityHeaders）。</summary>
	public static void ApplyResponses(Model model, StreamOptions options, HttpRequestMessage request)
	{
		if (string.IsNullOrWhiteSpace(options.SessionId)) return;

		if (Format(model) == "openrouter")
		{
			Add(request, "x-session-id", options.SessionId);
			return;
		}

		if (Format(model) == "openai") Add(request, "session_id", options.SessionId);
		Add(request, "x-client-request-id", options.SessionId);
	}

	/// <summary>Anthropic Messages 的亲和头：openrouter 用 x-session-id，其余用 x-session-affinity。</summary>
	public static void ApplyAnthropic(Model model, StreamOptions options, HttpRequestMessage request)
	{
		if (string.IsNullOrWhiteSpace(options.SessionId)) return;
		if (!Enabled(model, IsOpenRouter(model))) return;
		Add(request, Format(model) == "openrouter" ? "x-session-id" : "x-session-affinity", options.SessionId);
	}

	private static bool IsOpenRouter(Model model) =>
		string.Equals(model.Provider, "openrouter", StringComparison.Ordinal) ||
		model.BaseUrl.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);

	private static string Format(Model model) =>
		Text(model, "sessionAffinityFormat") ?? (IsOpenRouter(model) ? "openrouter" : "openai");

	private static bool Enabled(Model model, bool fallback) => Flag(model, "sendSessionAffinityHeaders") ?? fallback;

	private static string? Text(Model model, string key) =>
		model.Compat?[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static bool? Flag(Model model, string key) =>
		model.Compat?[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

	private static void Add(HttpRequestMessage request, string name, string value)
	{
		if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
	}
}

/// <summary>
/// GitHub Copilot 的动态请求头（静态头在模型目录的 headers 里，动态头必须按本次消息算）：
/// <c>X-Initiator</c>、<c>Openai-Intent</c>，含图片时追加 <c>Copilot-Vision-Request</c>。
/// </summary>
public static class CopilotHeaders
{
	public static void Apply(Model model, IReadOnlyList<Message> messages, HttpRequestMessage request)
	{
		if (!string.Equals(model.Provider, "github-copilot", StringComparison.Ordinal) &&
			!model.BaseUrl.Contains("githubcopilot.com", StringComparison.OrdinalIgnoreCase)) return;

		Add(request, "X-Initiator", messages.Count > 0 && messages[^1].Role != "user" ? "agent" : "user");
		Add(request, "Openai-Intent", "conversation-edits");
		if (HasImages(messages)) Add(request, "Copilot-Vision-Request", "true");
	}

	private static bool HasImages(IReadOnlyList<Message> messages)
	{
		foreach (var message in messages)
		{
			if (message is UserMessage user && user.Content.Blocks?.Any(static block => block is ImageContent) == true) return true;
			if (message is ToolResultMessage tool && tool.Content.Any(static block => block is ImageContent)) return true;
		}
		return false;
	}

	private static void Add(HttpRequestMessage request, string name, string value)
	{
		if (!request.Headers.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
	}
}
