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
		if (string.Equals(shareDestination, "radius", StringComparison.OrdinalIgnoreCase))
		{
			return (false, null, "Radius 会话上传尚未实现；设置 sessionShareDestination 为 gist 或配置 GitHub token。");
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
