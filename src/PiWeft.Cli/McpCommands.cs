using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Mcp;

namespace PiWeft.Cli;

/// <summary>
/// <c>pi mcp list|add|remove|login|logout</c>：管理 MCP 服务器配置与它们的 OAuth 令牌。
/// 与 <c>auth</c> 一样在通用参数解析之前分发，自带开关不会被主解析器当成未知选项。
/// </summary>
public static class McpCommands
{
	/// <summary>不需要取值的开关。</summary>
	private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "local", "json", "help" };

	private static readonly HttpClient Http = new(new SocketsHttpHandler
	{
		PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		ConnectTimeout = TimeSpan.FromSeconds(30),
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	/// <summary>顶层 <c>mcp</c> 命令。</summary>
	public static async Task<int?> RunAsync(
		string command,
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		string cwd,
		CancellationToken cancellationToken,
		string? agentDir = null)
	{
		// agent 目录可注入：测试因此不必改进程环境变量去隔离配置与令牌存储。
		var resolvedAgentDir = McpConfig.GetAgentDir(agentDir);
		return command switch
		{
			"list" => await ListAsync(rest, cwd, output, error, resolvedAgentDir, cancellationToken).ConfigureAwait(false),
			"add" => Add(rest, cwd, output, error, resolvedAgentDir),
			"remove" or "rm" => Remove(rest, cwd, output, error, resolvedAgentDir),
			"login" => await LoginAsync(rest, cwd, output, error, resolvedAgentDir, cancellationToken).ConfigureAwait(false),
			"logout" => Logout(rest, cwd, output, error, resolvedAgentDir),
			"help" or "--help" or "-h" => Usage(output),
			_ => Unknown(command, error),
		};
	}

	private static int Unknown(string command, TextWriter error)
	{
		error.WriteLine($"pi: 未知的 mcp 子命令「{command}」");
		Usage(error);
		return 2;
	}

	private static int Usage(TextWriter output)
	{
		output.WriteLine("""
			pi mcp 命令：
			  pi mcp list [--json] [--local]                列出 MCP 服务器与授权状态
			  pi mcp add <name> [options] -- <command> [args...]
			      [--env k=v]... [--cwd dir]                添加 stdio 服务器
			  pi mcp add <name> --url <url> [options]       添加 streamable HTTP 服务器
			      [--header k=v]... [--bearer-token-env-var <NAME>]
			      [--auth oauth] [--oauth-client-id id] [--oauth-client-secret secret]
			      [--scope a,b] [--exposure codemode|deferred|direct|hidden]
			      [--description text]
			  pi mcp remove <name> [--local]                移除服务器（同时清除它的令牌）
			  pi mcp login <name> [--timeout <seconds>]     为 HTTP 服务器走 OAuth 授权
			  pi mcp logout <name>                          删除保存的令牌

			默认写全局 <agentDir>/mcp.json；--local 改项目 .pi/mcp.json。
			OAuth 令牌存放在 <agentDir>/mcp-oauth.json。
			""");
		return 0;
	}

	private static async Task<int> ListAsync(
		IReadOnlyList<string> rest,
		string cwd,
		TextWriter output,
		TextWriter error,
		string agentDir,
		CancellationToken cancellationToken)
	{
		var (_, options, _) = Parse(rest);
		IReadOnlyList<McpServerConfig> configured;
		try
		{
			configured = McpConfig.Load(cwd, agentDir, includeProject: !options.ContainsKey("local"));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			error.WriteLine($"pi: 无法读取 MCP 配置：{exception.Message}");
			return 1;
		}

		McpConnection? connection = null;
		try
		{
			connection = await McpServers.ConnectConfiguredAsync(
				cwd,
				agentDir,
				onError: _ => { },
				cancellationToken,
				includeProject: !options.ContainsKey("local")).ConfigureAwait(false);

			var states = connection.Servers.ToDictionary(static state => state.Name, StringComparer.Ordinal);
			var failed = false;
			if (options.ContainsKey("json"))
			{
				var items = new List<JsonNode?>(configured.Count);
				foreach (var server in configured)
				{
					states.TryGetValue(server.Name, out var state);
					var token = server.Url is { Length: > 0 } endpoint ? McpOAuthStore.Load(endpoint, agentDir) : null;
					var tools = connection.Tools
						.Where(tool => tool.Name.StartsWith($"mcp__{McpToolAdapter.Sanitize(server.Name)}__", StringComparison.Ordinal))
						.Select(static tool => (JsonNode?)JsonValue.Create(tool.Name))
						.ToList();
					items.Add(new JsonObject
					{
						["name"] = server.Name,
						["transport"] = server.IsHttp ? "http" : "stdio",
						["url"] = server.Url,
						["command"] = server.Command,
						["enabled"] = server.Enabled,
						["exposure"] = server.Exposure,
						["state"] = state is null ? "disconnected" : state.Connected ? "connected" : "error",
						["tools"] = new JsonArray([.. tools]),
						["oauth"] = server.UsesOAuth,
						["authorized"] = token is not null,
						["error"] = state?.Error,
					});
					if (server.Enabled && state is not { Connected: true })
					{
						failed = true;
					}
				}

				output.WriteLine(new JsonObject { ["servers"] = new JsonArray([.. items]) }.ToJsonString());
				return failed ? 1 : 0;
			}

			if (configured.Count == 0)
			{
				output.WriteLine("没有配置 MCP 服务器。用 pi mcp add 添加。");
				return 0;
			}

			foreach (var server in configured)
			{
				states.TryGetValue(server.Name, out var state);
				var target = server.Url ?? string.Join(' ', new[] { server.Command }.Concat(server.Arguments).Where(static part => part is { Length: > 0 }));
				output.WriteLine($"{server.Name}  [{(server.IsHttp ? "http" : "stdio")}] {(state?.Connected == true ? "connected" : "disconnected")}  {target}");
				if (!server.Enabled)
				{
					output.WriteLine("    已停用");
				}

				if (state is { Connected: true })
				{
					var count = connection.Tools.Count(tool =>
						tool.Name.StartsWith($"mcp__{McpToolAdapter.Sanitize(server.Name)}__", StringComparison.Ordinal));
					output.WriteLine($"    工具 {count}");
				}
				else if (state?.Error is { Length: > 0 } reason)
				{
					output.WriteLine($"    错误：{reason}");
				}

				if (server.UsesOAuth)
				{
					output.WriteLine("    OAuth 服务器；需要登录时运行 pi mcp login " + server.Name);
				}

				if (server.Enabled && state is not { Connected: true })
				{
					failed = true;
				}
			}

			foreach (var message in connection.Errors)
			{
				error.WriteLine($"pi: mcp {message}");
			}

			return failed ? 1 : 0;
		}
		finally
		{
			if (connection is not null)
			{
				await connection.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private static int Add(IReadOnlyList<string> rest, string cwd, TextWriter output, TextWriter error, string agentDir)
	{
		var (name, options, trailing) = Parse(rest);
		if (name is not { Length: > 0 })
		{
			error.WriteLine("pi: mcp add 需要一个服务器名");
			return 2;
		}

		if (!McpConfig.IsValidName(name))
		{
			error.WriteLine("pi: 服务器名只能包含字母、数字、下划线与连字符");
			return 2;
		}

		// pi 形式：`add <name> [options] -- <command> [args...]`；`--command`/`--arg` 仍兼容。
		var url = First(options, "url");
		var command = First(options, "command") ?? (trailing.Count > 0 ? trailing[0] : null);
		if (url is null == (command is null))
		{
			error.WriteLine("pi: 必须且只能指定 --url 或 -- <command>");
			return 2;
		}

		if (!TryPairs(All(options, "header"), out var headers, out var problem) ||
			!TryPairs(All(options, "env"), out var environment, out problem))
		{
			error.WriteLine($"pi: 键值对格式应为 k=v（收到「{problem}」）");
			return 2;
		}

		// pi 的 `--bearer-token-env-var <NAME>`：运行时发送 `Authorization: Bearer ${NAME}`。
		if (First(options, "bearer-token-env-var") is { Length: > 0 } bearerVariable)
		{
			headers["Authorization"] = $"Bearer ${{{bearerVariable}}}";
		}

		if (First(options, "exposure") is { Length: > 0 } exposure &&
			!McpExposure.IsKnown(exposure))
		{
			error.WriteLine($"pi: 未知的 exposure「{exposure}」（可选 {McpExposure.Codemode} / {McpExposure.Deferred} / {McpExposure.Direct} / {McpExposure.Hidden}）");
			return 2;
		}

		// pi 的别名：--oauth-client-id / --oauth-client-secret。
		var clientId = First(options, "client-id") ?? First(options, "oauth-client-id");
		var clientSecret = First(options, "oauth-client-secret");

		var scopes = First(options, "scope");
		var arguments = command is null
			? (IReadOnlyList<string>)[]
			: [.. All(options, "arg"), .. trailing.Skip(1)];
		var authOption = First(options, "auth");
		var oauthConfig = url is not null && (string.Equals(authOption, "oauth", StringComparison.OrdinalIgnoreCase) ||
			clientId is not null || clientSecret is not null ||
			scopes is { Length: > 0 } || options.ContainsKey("oauth-callback-port") ||
			options.ContainsKey("oauth-client-name"))
				? new McpOAuthConfigEntry
				{
					ClientId = clientId,
					ClientSecret = clientSecret,
					Scope = scopes,
					CallbackPort = int.TryParse(First(options, "oauth-callback-port"), out var port) ? port : null,
					ClientName = First(options, "oauth-client-name"),
				}
				: null;

		var entry = new McpServerEntry
		{
			Url = url,
			Command = command,
			Args = command is null ? null : arguments,
			Headers = headers.Count > 0 ? headers : null,
			Env = command is not null && environment.Count > 0 ? environment : null,
			Cwd = First(options, "cwd"),
			Auth = command is null && authOption is not null &&
				!string.Equals(authOption, "oauth", StringComparison.OrdinalIgnoreCase) ? authOption : null,
			ClientId = clientId,
			ClientSecret = clientSecret,
			OAuth = oauthConfig,
			Description = First(options, "description"),
			Exposure = First(options, "exposure"),
			Scopes = scopes is { Length: > 0 }
				? [.. scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
				: null,
		};

		var path = options.ContainsKey("local") ? McpConfig.ProjectPath(cwd) : McpConfig.UserPath(agentDir);
		try
		{
			McpConfig.SaveEntry(path, name, entry);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			error.WriteLine($"pi: 无法写入 {path}：{exception.Message}");
			return 1;
		}

		output.WriteLine($"已写入 {path}");
		output.WriteLine($"    {name} → {url ?? command}");
		if (oauthConfig is not null ||
			string.Equals(entry.Auth, McpServerConfig.OAuthAuthValue, StringComparison.OrdinalIgnoreCase) ||
			(clientId is not null && url is not null))
		{
			output.WriteLine($"    该服务器使用 OAuth：运行 pi mcp login {name} 完成授权。");
		}

		return 0;
	}

	private static int Remove(IReadOnlyList<string> rest, string cwd, TextWriter output, TextWriter error, string agentDir)
	{
		var (name, options, _) = Parse(rest);
		if (name is not { Length: > 0 })
		{
			error.WriteLine("pi: mcp remove 需要一个服务器名");
			return 2;
		}

		var known = McpConfig.Load(cwd, agentDir, includeProject: true)
			.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.Ordinal));

		var path = options.ContainsKey("local") ? McpConfig.ProjectPath(cwd) : McpConfig.UserPath(agentDir);
		try
		{
			if (!McpConfig.RemoveEntry(path, name))
			{
				error.WriteLine($"pi: {path} 里没有服务器「{name}」");
				return 1;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			error.WriteLine($"pi: 无法写入 {path}：{exception.Message}");
			return 1;
		}

		// 与 pi 一致：移除配置不会删除已保存的 OAuth 凭据。
		output.WriteLine($"已移除 {name}");
		return 0;
	}

	private static async Task<int> LoginAsync(
		IReadOnlyList<string> rest,
		string cwd,
		TextWriter output,
		TextWriter error,
		string agentDir,
		CancellationToken cancellationToken)
	{
		var (name, options, _) = Parse(rest);
		if (name is not { Length: > 0 })
		{
			error.WriteLine("pi: mcp login 需要一个服务器名");
			return 2;
		}

		var server = McpConfig.Load(cwd, agentDir, includeProject: true)
			.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

		if (server is null)
		{
			error.WriteLine($"pi: 找不到 MCP 服务器「{name}」");
			return 1;
		}

		if (!server.IsHttp)
		{
			error.WriteLine("pi: 只有 HTTP 服务器支持 OAuth 登录");
			return 2;
		}

		// pi 的 --timeout 限定整个登录过程（含对授权服务器的请求），不只是等浏览器回来。
		CancellationTokenSource? timeoutSource = null;
		if (double.TryParse(First(options, "timeout"), NumberStyles.Float, CultureInfo.InvariantCulture, out var timeoutSeconds) &&
			timeoutSeconds > 0)
		{
			timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
			cancellationToken = timeoutSource.Token;
		}

		try
		{
			return await LoginAsyncInner(server, name, agentDir, output, error, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeoutSource is not null && timeoutSource.IsCancellationRequested)
		{
			error.WriteLine($"pi: 登录超时（--timeout {timeoutSeconds:0} 秒）");
			return 1;
		}
		finally
		{
			timeoutSource?.Dispose();
		}
	}

	private static async Task<int> LoginAsyncInner(
		McpServerConfig server,
		string name,
		string agentDir,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		var challenge = await ProbeChallengeAsync(server.Url!, cancellationToken).ConfigureAwait(false);
		var metadata = await McpOAuth.DiscoverAsync(
			Http,
			server.Url!,
			challenge,
			cancellationToken,
			server.OAuth?.AuthServerMetadataUrl).ConfigureAwait(false);
		if (metadata is null)
		{
			error.WriteLine("pi: 无法发现授权服务器元数据（检查网络或服务器的 WWW-Authenticate）");
			return 1;
		}

		// 配置了 clientId + clientSecret 的服务器直接走客户端凭据授权，无需浏览器参与。
		if (McpOAuth.UsesClientCredentials(server))
		{
			var granted = await McpOAuth
				.ClientCredentialsAsync(Http, server.Url!, server, agentDir, cancellationToken, metadata)
				.ConfigureAwait(false);
			if (!granted.Ok)
			{
				error.WriteLine($"pi: 客户端凭据授权失败：{granted.Error}");
				return 1;
			}

			output.WriteLine($"已登录 {name}（客户端凭据授权），令牌保存在 {McpOAuthStore.Path(agentDir)}");
			return 0;
		}

		var result = await McpOAuth.AuthorizeAsync(
			Http,
			server.Url!,
			server,
			metadata,
			agentDir,
			url =>
			{
				output.WriteLine("请在浏览器中完成授权；若没有自动打开，请手动访问：");
				output.WriteLine($"    {url}");
				output.Flush();
			},
			cancellationToken).ConfigureAwait(false);

		if (!result.Ok)
		{
			error.WriteLine($"pi: 授权失败：{result.Error}");
			return 1;
		}

		output.WriteLine($"已登录 {name}，令牌保存在 {McpOAuthStore.Path(agentDir)}");
		return 0;
	}

	private static int Logout(IReadOnlyList<string> rest, string cwd, TextWriter output, TextWriter error, string agentDir)
	{
		var (name, _, _) = Parse(rest);
		if (name is not { Length: > 0 })
		{
			error.WriteLine("pi: mcp logout 需要一个服务器名");
			return 2;
		}

		var server = McpConfig.Load(cwd, agentDir, includeProject: true)
			.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));

		if (server?.Url is not { Length: > 0 } endpoint)
		{
			error.WriteLine($"pi: 找不到使用 HTTP 的 MCP 服务器「{name}」");
			return 1;
		}

		output.WriteLine(McpOAuthStore.Remove(endpoint, agentDir)
			? $"已登出 {name}"
			: $"{name} 没有保存的令牌");
		return 0;
	}

	/// <summary>发一次不带令牌的 initialize，从 401 响应里取出 <c>WWW-Authenticate</c>。</summary>
	private static async Task<string?> ProbeChallengeAsync(string url, CancellationToken cancellationToken)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = new StringContent(
					"""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"PiWeft","version":"0.1.0"}}}""",
					Encoding.UTF8,
					"application/json"),
			};
			request.Headers.TryAddWithoutValidation("accept", "application/json, text/event-stream");

			using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			return response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(' ', values) : null;
		}
		catch (HttpRequestException)
		{
			return null;
		}
	}

	/// <summary>
	/// 解析子命令参数。<c>--</c> 之后的参数原样进入 <paramref name="trailing"/>（pi 的
	/// <c>pi mcp add name -- command args...</c> 形式），不再当成选项或位置参数。
	/// </summary>
	private static (string? Positional, Dictionary<string, List<string>> Options, IReadOnlyList<string> Trailing) Parse(
		IReadOnlyList<string> rest)
	{
		var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		string? positional = null;
		List<string>? trailing = null;

		for (var index = 0; index < rest.Count; index++)
		{
			var item = rest[index];
			if (trailing is not null)
			{
				trailing.Add(item);
				continue;
			}

			if (item == "--")
			{
				trailing = [];
				continue;
			}

			if (item == "-l")
			{
				Add(options, "local", "true");
				continue;
			}

			if (!item.StartsWith("--", StringComparison.Ordinal))
			{
				positional ??= item;
				continue;
			}

			var name = item[2..];
			var separator = name.IndexOf('=');
			if (separator > 0)
			{
				Add(options, name[..separator], name[(separator + 1)..]);
			}
			else if (Flags.Contains(name))
			{
				Add(options, name, "true");
			}
			else
			{
				Add(options, name, index + 1 < rest.Count ? rest[++index] : string.Empty);
			}
		}

		return (positional, options, trailing ?? []);

		static void Add(Dictionary<string, List<string>> target, string key, string value)
		{
			if (!target.TryGetValue(key, out var values))
			{
				target[key] = values = [];
			}

			values.Add(value);
		}
	}

	private static string? First(Dictionary<string, List<string>> options, string name) =>
		options.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

	private static IReadOnlyList<string> All(Dictionary<string, List<string>> options, string name) =>
		options.TryGetValue(name, out var values) ? values : [];

	private static bool TryPairs(IReadOnlyList<string> items, out Dictionary<string, string> pairs, out string? problem)
	{
		pairs = new Dictionary<string, string>(StringComparer.Ordinal);
		problem = null;

		foreach (var item in items)
		{
			var separator = item.IndexOf('=');
			if (separator <= 0)
			{
				problem = item;
				return false;
			}

			pairs[item[..separator]] = item[(separator + 1)..];
		}

		return true;
	}
}
