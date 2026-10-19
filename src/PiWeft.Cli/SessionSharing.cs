using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent;

namespace PiWeft.Cli;

/// <summary>
/// <c>/share</c>：把会话 JSONL 上传为私有 GitHub gist。Radius 目标需要 Pi 的 Radius OAuth，
/// 未登录时明确提示；配置为 auto 时优先 gist。
/// </summary>
public static class SessionSharing
{
	public static async Task<(bool Ok, string? Url, string? Error)> ShareAsync(
		CodingAgentSession session,
		string destination,
		CancellationToken cancellationToken = default)
	{
		var shareDestination = destination is "gist" or "radius" ? destination : session.Settings.SessionShareDestination;
		if (string.Equals(shareDestination, "auto", StringComparison.OrdinalIgnoreCase))
		{
			shareDestination = PiWeft.Ai.Auth.OAuthStore.Read("radius") is not null ? "radius" : "gist";
		}

		if (string.Equals(shareDestination, "radius", StringComparison.OrdinalIgnoreCase))
		{
			return await ShareViaRadiusAsync(session, cancellationToken).ConfigureAwait(false);
		}

		var content = ReadSessionContent(session);
		if (content is null)
		{
			return (false, null, "会话尚未落盘，无法上传。");
		}

		var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ??
			Environment.GetEnvironmentVariable("GH_TOKEN");
		if (string.IsNullOrEmpty(token))
		{
			return (false, null, "缺少 GITHUB_TOKEN / GH_TOKEN，无法创建私有 gist。");
		}

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/gists");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.TryAddWithoutValidation("accept", "application/vnd.github+json");
		request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

		var body = new JsonObject
		{
			["public"] = false,
			["files"] = new JsonObject
			{
				[$"{session.Session.SessionId}.jsonl"] = new JsonObject { ["content"] = content },
			},
		};

		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return (false, null, $"GitHub gist 创建失败：HTTP {(int)response.StatusCode} {Summarize(text)}");
			}

			var node = JsonNode.Parse(text);
			return node?["html_url"]?.GetValue<string>() is { Length: > 0 } url
				? (true, url, null)
				: (false, null, "GitHub 响应缺少 html_url");
		}
		catch (OperationCanceledException)
		{
			return (false, null, "上传已取消");
		}
		catch (HttpRequestException exception)
		{
			return (false, null, exception.Message);
		}
		catch (JsonException)
		{
			return (false, null, "GitHub 返回了无法解析的响应");
		}
	}

	private static async Task<(bool Ok, string? Url, string? Error)> ShareViaRadiusAsync(
		CodingAgentSession session,
		CancellationToken cancellationToken)
	{
		var credential = PiWeft.Ai.Auth.OAuthStore.Read("radius");
		if (credential is null)
		{
			return (false, null, "未登录 Radius；先运行 pi login radius，或把 sessionShareDestination 设为 gist。");
		}

		var gateway = (Environment.GetEnvironmentVariable("PI_RADIUS_GATEWAY") is { Length: > 0 } value
			? value
			: "https://radius.pi.dev").TrimEnd('/');
		var html = SessionHtml.Render(session);
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{gateway}/v1/artifacts?visibility=organization&title=Pi%20session");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {credential.Access}");
		request.Content = new StringContent(html, Encoding.UTF8, "application/x-ndjson");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			JsonNode? json = null;
			try { json = body.Length > 0 ? JsonNode.Parse(body) : null; } catch (JsonException) { }
			if (!response.IsSuccessStatusCode)
			{
				var detail = json?["error"]?.GetValue<string>() ?? Summarize(body);
				return (false, null, $"Radius 上传失败：HTTP {(int)response.StatusCode} {detail}");
			}

			return json?["artifact"]?["canonical_url"]?.GetValue<string>() is { Length: > 0 } url
				? (true, url, null)
				: (false, null, "Radius 响应缺少 artifact.canonical_url");
		}
		catch (OperationCanceledException)
		{
			return (false, null, "上传已取消");
		}
		catch (HttpRequestException exception)
		{
			return (false, null, exception.Message);
		}
	}

	private static string? ReadSessionContent(CodingAgentSession session)
	{
		var path = session.Session.SessionFile;
		if (path is not { Length: > 0 } || !File.Exists(path))
		{
			return null;
		}

		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string Summarize(string body)
	{
		var trimmed = body.Trim().Replace('\n', ' ');
		return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
	}
}
