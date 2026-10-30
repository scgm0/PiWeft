using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;

namespace PiWeft.Cli;

/// <summary>
/// <c>/share</c>：把**当前分支**导出成带 <c>pi.share</c> 尾条目的 JSONL，上传为私有 gist 或
/// Radius artifact，并返回 pi 的会话查看器地址。
/// <para>
/// 与 pi 的差异只在传输层：pi 用 <c>gh gist create</c>，这里优先用 <c>gh</c>、没有时退回
/// <c>GITHUB_TOKEN</c> 的 HTTP API。产物语义保持一致 —— 整份会话文件里可能有多条分支，
/// 查看器要的是当前这条；系统提示词与工具 schema 不在会话文件里，必须由尾条目补上。
/// </para>
/// </summary>
public static class SessionSharing
{
	/// <summary>pi 的默认查看器地址；可用 <c>PI_SHARE_VIEWER_URL</c> 覆盖。</summary>
	private const string DefaultViewerUrl = "https://pi.dev/session/";

	private const string DefaultRadiusGateway = "https://radius.pi.dev";

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

		var jsonl = BuildShareJsonl(session);
		if (string.Equals(shareDestination, "radius", StringComparison.OrdinalIgnoreCase))
		{
			return await ShareViaRadiusAsync(jsonl, cancellationToken).ConfigureAwait(false);
		}

		return await ShareViaGistAsync(jsonl, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// 导出当前分支 + <c>pi.share</c> 尾条目。头部用当前时间戳（与 pi 的
	/// <c>serializeSessionBranch</c> 一致），条目按分支顺序重挂 <c>parentId</c>，第一条为根。
	/// </summary>
	internal static string BuildShareJsonl(CodingAgentSession session)
	{
		var timestamp = SessionPaths.ToIso8601(DateTimeOffset.UtcNow);
		var builder = new StringBuilder();
		builder.Append(SessionJson.Serialize(new SessionHeader
		{
			Id = session.Session.SessionId,
			Timestamp = timestamp,
			Cwd = session.Session.Cwd,
		})).Append('\n');

		string? parentId = null;
		foreach (var entry in session.Session.GetBranch())
		{
			builder.Append(SessionJson.Serialize(WithParent(entry, parentId))).Append('\n');
			parentId = entry.Id;
		}

		var data = new JsonObject
		{
			["systemPrompt"] = session.CurrentSystemPrompt,
			["tools"] = new JsonArray([.. session.Tools.Select(static tool => (JsonNode)new JsonObject
			{
				["name"] = tool.Name,
				["description"] = tool.Description,
				["parameters"] = tool.Parameters.DeepClone(),
			})]),
		};

		builder.Append(SessionJson.Serialize(new CustomEntry
		{
			Id = Guid.NewGuid().ToString("N")[..8],
			ParentId = parentId,
			Timestamp = timestamp,
			CustomType = "pi.share",
			Data = data,
		})).Append('\n');

		return builder.ToString();
	}

	/// <summary>
	/// 分支里的父链重挂到导出的前一条。未知条目整行输出原始对象，
	/// 只改模型的 <c>ParentId</c> 不会落到文件里，必须一起改。
	/// </summary>
	private static SessionEntry WithParent(SessionEntry entry, string? parentId)
	{
		if (entry.ParentId == parentId)
		{
			return entry;
		}

		if (entry is not UnknownSessionEntry unknown)
		{
			return entry with { ParentId = parentId };
		}

		var raw = (JsonObject)unknown.Raw.DeepClone();
		raw["parentId"] = parentId is null ? null : JsonValue.Create(parentId);
		return unknown with { ParentId = parentId, Raw = raw };
	}

	/// <summary>查看器链接：<c>PI_SHARE_VIEWER_URL</c> 或 pi 的默认地址，片段是 gist id。</summary>
	internal static string ViewerUrl(string gistId)
	{
		var baseUrl = Environment.GetEnvironmentVariable("PI_SHARE_VIEWER_URL") is { Length: > 0 } configured
			? configured
			: DefaultViewerUrl;
		return $"{baseUrl}#{gistId}";
	}

	private static async Task<(bool Ok, string? Url, string? Error)> ShareViaGistAsync(
		string jsonl,
		CancellationToken cancellationToken)
	{
		var (ghUrl, ghError) = await CreateGistWithGhAsync(jsonl, cancellationToken).ConfigureAwait(false);
		var gistUrl = ghUrl;
		string? tokenError = null;
		if (gistUrl is null)
		{
			var (tokenUrl, error) = await CreateGistWithTokenAsync(jsonl, cancellationToken).ConfigureAwait(false);
			gistUrl = tokenUrl;
			tokenError = error;
		}

		if (gistUrl is null)
		{
			// gh 没登录/没安装，token 也没有：把两条线索都告诉用户，而不是只说「失败」。
			var reason = ghError ?? tokenError ?? "无法创建 gist。";
			return (false, null, reason + " 先运行 gh auth login，或设置 GITHUB_TOKEN / GH_TOKEN。");
		}

		var gistId = gistUrl.Split('/').LastOrDefault(static segment => segment.Length > 0);
		return gistId is { Length: > 0 }
			? (true, ViewerUrl(gistId), null)
			: (false, null, $"无法从 gist 地址解析 id：{gistUrl}");
	}

	private static async Task<(string? GistUrl, string? Error)> CreateGistWithGhAsync(
		string jsonl,
		CancellationToken cancellationToken)
	{
		var path = Path.Combine(Path.GetTempPath(), $"piweft-share-{Guid.NewGuid():N}.jsonl");
		try
		{
			await File.WriteAllTextAsync(path, jsonl, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

			var status = await RunAsync("gh", ["auth", "status"], cancellationToken).ConfigureAwait(false);
			if (status is null || status.Value.ExitCode != 0)
			{
				// 没有 gh / 未登录：交给 token 路径；错误留作两条路径都失败时的说明。
				return (null, status is { } present
					? $"gh 未登录：{Summarize(present.Error)}"
					: null);
			}

			var create = await RunAsync("gh", ["gist", "create", "--public=false", path], cancellationToken)
				.ConfigureAwait(false);
			if (create is null)
			{
				return (null, null);
			}

			if (create.Value.ExitCode != 0)
			{
				return (null, $"gh gist create 失败：{Summarize(create.Value.Error)}");
			}

			var url = create.Value.Output
				.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.LastOrDefault();
			return url is { Length: > 0 } ? (url, null) : (null, "gh 没有输出 gist 地址");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (null, $"写入临时文件失败：{exception.Message}");
		}
		finally
		{
			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 清理失败不影响分享结果。
			}
		}
	}

	private static async Task<(string? GistUrl, string? Error)> CreateGistWithTokenAsync(
		string jsonl,
		CancellationToken cancellationToken)
	{
		var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ??
			Environment.GetEnvironmentVariable("GH_TOKEN");
		if (string.IsNullOrEmpty(token))
		{
			return (null, "缺少 GITHUB_TOKEN / GH_TOKEN");
		}

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/gists");
		request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
		request.Headers.TryAddWithoutValidation("accept", "application/vnd.github+json");
		request.Headers.TryAddWithoutValidation("user-agent", "PiWeft/0.1.0");

		var body = new JsonObject
		{
			["public"] = false,
			["files"] = new JsonObject
			{
				["session.jsonl"] = new JsonObject { ["content"] = jsonl },
			},
		};

		request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		try
		{
			using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return (null, $"GitHub gist 创建失败：HTTP {(int)response.StatusCode} {Summarize(text)}");
			}

			return JsonNode.Parse(text)?["html_url"]?.GetValue<string>() is { Length: > 0 } url
				? (url, null)
				: (null, "GitHub 响应缺少 html_url");
		}
		catch (OperationCanceledException)
		{
			return (null, "上传已取消");
		}
		catch (HttpRequestException exception)
		{
			return (null, exception.Message);
		}
	}

	private static async Task<(bool Ok, string? Url, string? Error)> ShareViaRadiusAsync(
		string jsonl,
		CancellationToken cancellationToken)
	{
		var credential = PiWeft.Ai.Auth.OAuthStore.Read("radius");
		if (credential is null)
		{
			return (false, null, "未登录 Radius；先运行 pi login radius，或把 sessionShareDestination 设为 gist。");
		}

		var gateway = (Environment.GetEnvironmentVariable("PI_RADIUS_GATEWAY") is { Length: > 0 } value
			? value
			: DefaultRadiusGateway).TrimEnd('/');
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		using var request = new HttpRequestMessage(
			HttpMethod.Post,
			$"{gateway}/v1/artifacts?visibility=organization&title=Pi%20session");
		request.Headers.TryAddWithoutValidation("authorization", $"Bearer {credential.Access}");
		request.Content = new StringContent(jsonl, Encoding.UTF8, "application/x-ndjson");
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

	/// <summary>运行一个外部命令并捕获输出；可执行文件不存在时返回 <see langword="null"/>。</summary>
	private static async Task<(int ExitCode, string Output, string Error)?> RunAsync(
		string fileName,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo(fileName)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = new Process { StartInfo = startInfo };
		try
		{
			if (!process.Start())
			{
				return null;
			}
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}

		// 不把取消令牌交给 ReadToEndAsync：命令被杀掉时流会自然结束，
		// 提前取消只会让读取任务抛异常、把已收集到的输出一起丢掉。
		var output = process.StandardOutput.ReadToEndAsync();
		var error = process.StandardError.ReadToEndAsync();

		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		try
		{
			await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
				// 已经退出或没有权限，忽略。
			}

			if (!cancellationToken.IsCancellationRequested)
			{
				return (1, string.Empty, "命令超时（30 秒）");
			}

			throw;
		}

		return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
	}

	private static string Summarize(string body)
	{
		var trimmed = body.Trim().Replace('\n', ' ');
		return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
	}
}
