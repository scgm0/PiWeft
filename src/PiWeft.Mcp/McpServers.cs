using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.Mcp;

/// <summary>一个 stdio MCP 服务器进程及其客户端。</summary>
public sealed class McpProcess : IAsyncDisposable
{
	private readonly Process _process;

	private McpProcess(Process process, McpClient client, McpServerConfig config)
	{
		_process = process;
		Client = client;
		Config = config;
	}

	public McpClient Client { get; }

	public McpServerConfig Config { get; }

	/// <summary>启动 stdio 服务器并完成 MCP 握手。</summary>
	public static async Task<McpProcess> StartAsync(McpServerConfig config, CancellationToken cancellationToken)
	{
		if (!config.IsStdio)
		{
			throw new InvalidOperationException($"MCP server '{config.Name}' has no command to start");
		}

		// 图形外壳启动时进程只继承最小 PATH：把登录 shell PATH 与常见目录补进来，并把命令
		// 解析成绝对路径，否则 `"command": "node"` 会以 ENOENT 失败（见 McpLaunchEnvironment）。
		var (fileName, path) = await McpLaunchEnvironment.PrepareAsync(config.Command!).ConfigureAwait(false);

		var startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in config.Arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		// 服务器自己也常要拉起 node/npx 子进程，PATH 必须传给子进程。
		startInfo.Environment["PATH"] = path;
		startInfo.Environment["MCP_CLIENT"] = "PiWeft";
		foreach (var (name, value) in config.Env ?? new Dictionary<string, string>())
		{
			startInfo.Environment[name] = value;
		}

		if (config.Cwd is { Length: > 0 } cwd)
		{
			startInfo.WorkingDirectory = Path.IsPathRooted(cwd) ? cwd : Path.GetFullPath(cwd);
		}

		var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

		if (!process.Start())
		{
			throw new InvalidOperationException($"Failed to start MCP server '{config.Name}' ({config.Command})");
		}

		// 持续读取 stderr，避免话多的服务器因管道写满而阻塞；同时保留一份有界摘要，
		// 进程异常退出时挂起请求能带上退出码与最后几行 stderr，而不是只有一句
		// "connection closed"——那句话没有任何可操作的信息（见 McpClient.ClosureDiagnostics）。
		var stderrTail = new StderrTail(capacity: 4000);
		var exitDetail = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = Task.Run(async () =>
		{
			try
			{
				while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
				{
					stderrTail.Append(line);
				}
			}
			catch (Exception)
			{
				// 进程已退出。
			}

			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (Exception)
			{
				// 等不到退出码就用已知信息：进程退出（stderr 已关闭）这一事实本身仍然有用。
			}

			string detail;
			try
			{
				detail = process.HasExited
					? $"process exited with code {process.ExitCode}"
					: "process exited";
			}
			catch (Exception)
			{
				detail = "process exited";
			}

			var tail = stderrTail.ToString();
			exitDetail.TrySetResult(tail.Length > 0 ? $"{detail}: {tail}" : detail);
		});

		McpClient client;
		try
		{
			client = await McpClient.ConnectAsync(
				process.StandardOutput.BaseStream,
				process.StandardInput.BaseStream,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			// 握手失败时尽量带上服务器的退出码与 stderr 摘要：正在崩溃的进程几乎都已退出，
			// 短等一拍就够；还活着的服务器按失败原因本身处理（超时/协议错误），杀掉后不猜退出码。
			var detail = await ExitDetailAsync(process, stderrTail, wait: TimeSpan.FromMilliseconds(500))
				.ConfigureAwait(false);

			// 握手失败时必须杀掉已经拉起来的进程：否则它会一直挂着，占住内存与管道。
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch (Exception killException) when (killException is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
			{
				// 进程已经退出，或平台不支持整树杀进程。
			}

			process.Dispose();

			if (detail.Length > 0 && exception is IOException closed)
			{
				throw new IOException($"{closed.Message} ({detail})", closed);
			}

			throw;
		}

		// 进程退出后把退出码与 stderr 摘要挂到客户端上：握手期间或会话中途崩溃的
		// 挂起请求都能带上原因。
		_ = exitDetail.Task.ContinueWith(
			static (task, state) => ((McpClient)state!).ClosureDiagnostics = task.Result,
			client,
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);

		client.ConfiguredServerName = config.Name;
		return new McpProcess(process, client, config);
	}

	/// <summary>
	/// 汇总服务器进程的退出诊断：退出码与 stderr 尾部。先给正在退出的进程一小段时间，
	/// 超时后按仍存活处理；两者都拿不到就返回空串，调用方沿用原始错误。
	/// </summary>
	private static async Task<string> ExitDetailAsync(Process process, StderrTail stderrTail, TimeSpan wait)
	{
		try
		{
			using var timeout = new CancellationTokenSource(wait);
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// 没等到退出就按仍存活处理，不猜退出码。
		}

		string? code = null;
		try
		{
			if (process.HasExited)
			{
				code = $"process exited with code {process.ExitCode}";
			}
		}
		catch (Exception)
		{
			// 退出码拿不到就只报 stderr。
		}

		var tail = stderrTail.ToString();
		return (code, tail.Length) switch
		{
			(null, 0) => string.Empty,
			(null, _) => tail,
			(_, 0) => code!,
			(_, _) => $"{code}: {tail}",
		};
	}

	public async ValueTask DisposeAsync()
	{
		await Client.DisposeAsync().ConfigureAwait(false);

		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
			// 进程已经退出。
		}
		catch (NotSupportedException)
		{
			// 该平台不支持整树杀进程。
		}

		_process.Dispose();
	}

	/// <summary>有界的 stderr 尾部缓冲：只保留最后若干行，供崩溃诊断使用。</summary>
	private sealed class StderrTail(int capacity)
	{
		private const int MaxLineLength = 500;

		private readonly object _gate = new();
		private readonly Queue<string> _lines = new();
		private int _length;

		public void Append(string line)
		{
			if (line.Length > MaxLineLength)
			{
				line = string.Concat(line.AsSpan(0, MaxLineLength), "…");
			}

			lock (_gate)
			{
				_lines.Enqueue(line);
				_length += line.Length + 3;
				while (_length > capacity && _lines.Count > 1)
				{
					_length -= _lines.Dequeue().Length + 3;
				}
			}
		}

		public override string ToString()
		{
			lock (_gate)
			{
				return string.Join(" | ", _lines);
			}
		}
	}
}

/// <summary>把 MCP 工具适配成代理的工具形状。</summary>
public static class McpToolAdapter
{
	/// <summary>注册名：<c>mcp__&lt;服务器&gt;__&lt;工具&gt;</c>，其余字符替换为 <c>_</c>。</summary>
	public static string ToolName(string server, string tool) =>
		$"mcp__{Sanitize(server)}__{Sanitize(tool)}";

	/// <summary>codemode 命名空间名，与 pi 一致：<c>mcp__&lt;server&gt;</c>。</summary>
	public static string NamespaceName(string server) => $"mcp__{Sanitize(server)}";

	public static string Sanitize(string value)
	{
		var buffer = new char[value.Length];
		for (var i = 0; i < value.Length; i++)
		{
			var c = value[i];
			buffer[i] = char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_';
		}

		return new string(buffer);
	}

	/// <summary>把单个 MCP 工具包装成代理工具，调用会回到服务器。</summary>
	public static AgentTool Create(
		IMcpToolInvoker client,
		string server,
		McpTool tool,
		TimeSpan? timeout = null,
		string? exposure = null,
		string? namespaceDescription = null,
		string? namespaceInstructions = null)
	{
		var name = ToolName(server, tool.Name);
		var requestTimeout = timeout;
		var description = tool.Description.Length > 0
			? tool.Description
			: $"MCP tool '{tool.Name}' from server '{server}'";

		return new AgentTool
		{
			Name = name,
			Label = name,
			Description = description,
			Parameters = (JsonObject)tool.InputSchema.DeepClone(),
			OutputSchema = tool.OutputSchema is { } outputSchema ? (JsonObject)outputSchema.DeepClone() : null,
			Exposure = exposure ?? ToolExposure.Direct,
			// 与 pi 一致：命名空间是 mcp__<server>，服务器原名作为别名。
			Namespace = NamespaceName(server),
			NamespaceAliases = [server],
			NamespaceDescription = string.IsNullOrWhiteSpace(namespaceDescription) ? null : namespaceDescription,
			NamespaceInstructions = string.IsNullOrWhiteSpace(namespaceInstructions) ? null : namespaceInstructions,
			Execute = async invocation =>
			{
				using var timeoutSource = requestTimeout is not null
					? CancellationTokenSource.CreateLinkedTokenSource(invocation.CancellationToken)
					: null;

				if (requestTimeout is { } span)
				{
					timeoutSource?.CancelAfter(span);
				}
				var token = timeoutSource?.Token ?? invocation.CancellationToken;

				try
				{
					var result = await client
						.CallToolAsync(tool.Name, invocation.Arguments, token)
						.ConfigureAwait(false);

					// 工具失败时协议层仍算正常返回，但对模型来说是一次错误结果。
					// 没有 content 时回退到 structuredContent 的 JSON（pi 的 toLlmContent 行为）。
					var content = result.Content.Count > 0
						? result.Content
						: result.StructuredContent is { } structured
							? [new TextContent { Text = structured.ToJsonString() }]
							: [new TextContent { Text = result.IsError ? "MCP tool failed" : "(no output)" }];

					return new AgentToolResult
					{
						Content = content,
						IsError = result.IsError,
						StructuredContent = result.StructuredContent?.DeepClone(),
						Details = new JsonObject
						{
							["server"] = server,
							["tool"] = tool.Name,
							["result"] = result.Raw,
						},
					};
				}
				catch (McpException ex)
				{
					return AgentToolResult.Error($"MCP error from '{server}': {ex.Message}");
				}
				catch (OperationCanceledException) when (!invocation.CancellationToken.IsCancellationRequested)
				{
					return AgentToolResult.Error($"MCP tool '{name}' timed out");
				}
			},
		};
	}
}

/// <summary>供界面展示的单个服务器状态。</summary>
public sealed record McpServerState(string Name, bool Connected, int ToolCount, string? Error);

/// <summary>已连接的 MCP 服务器及其暴露的工具。</summary>
public sealed class McpConnection : IAsyncDisposable
{
	private readonly List<IAsyncDisposable> _transports;
	private readonly List<ServerEntry> _servers;
	private readonly List<AgentTool> _tools;
	private readonly List<IMcpResourceProvider> _resourceServers;
	private readonly List<string> _errors;
	private readonly List<McpServerState> _states;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);

	internal McpConnection(
		List<IAsyncDisposable> transports,
		List<ServerEntry> servers,
		List<AgentTool> tools,
		List<IMcpResourceProvider> resourceServers,
		List<string> errors,
		List<McpServerState> states)
	{
		_transports = transports;
		_servers = servers;
		_tools = tools;
		_resourceServers = resourceServers;
		_errors = errors;
		_states = states;
	}

	/// <summary>一个已连接的服务器，保留它以便列表变化后重新列举工具。</summary>
	internal sealed record ServerEntry(string Name, IMcpToolInvoker Client, TimeSpan Timeout);

	/// <summary>
	/// 所有已连接服务器的工具，已加命名空间。服务器发出 <c>tools/list_changed</c> 时，
	/// 同一个列表实例会被就地更新，因此持有该引用的地方都能看到刷新结果。
	/// </summary>
	public IReadOnlyList<AgentTool> Tools => _tools;

	/// <summary>暴露资源的已连接服务器，供资源工具使用。</summary>
	public IReadOnlyList<IMcpResourceProvider> ResourceServers => _resourceServers;

	/// <summary>每个配置过的服务器的连接状态与工具数，供界面展示。</summary>
	public IReadOnlyList<McpServerState> Servers => _states;

	/// <summary>未能连接的服务器，格式为 <c>名称: 原因</c>；另外包含刷新失败。</summary>
	public IReadOnlyList<string> Errors => _errors;

	/// <summary>服务器宣告变化并刷新工具列表之后触发。</summary>
	public event Action<McpConnection>? ToolsChanged;

	/// <summary>服务器宣告 <c>resources/list_changed</c> 时触发。</summary>
	public event Action<McpConnection, string>? ResourcesChanged;

	/// <summary>服务器宣告 <c>prompts/list_changed</c> 时触发。</summary>
	public event Action<McpConnection, string>? PromptsChanged;

	/// <summary>重新列举所有已连接服务器的工具，并触发 <see cref="ToolsChanged"/>。</summary>
	public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
	{
		await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var next = new List<AgentTool>();
			foreach (var server in _servers)
			{
				foreach (var tool in await server.Client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
				{
					next.Add(McpToolAdapter.Create(server.Client, server.Name, tool, server.Timeout));
				}
			}

			_tools.Clear();
			_tools.AddRange(next);

			// 刷新工具数，界面上的状态跟着更新。
			for (var index = 0; index < _states.Count; index++)
			{
				var state = _states[index];
				if (state.Connected)
				{
					var prefix = $"mcp__{McpToolAdapter.Sanitize(state.Name)}__";
					_states[index] = state with { ToolCount = next.Count(tool => tool.Name.StartsWith(prefix, StringComparison.Ordinal)) };
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// 刷新失败时保留原先的工具列表，与 pi 的行为一致。
			_errors.Add($"tools refresh: {ex.Message}");
			return;
		}
		finally
		{
			_refreshGate.Release();
		}

		ToolsChanged?.Invoke(this);
	}

	/// <summary>处理服务器通知。它运行在传输的读取循环上，因此只启动工作而不等待。</summary>
	internal void OnNotification(string server, string method)
	{
		switch (method)
		{
			case "notifications/tools/list_changed":
				_ = RefreshToolsAsync();
				break;

			case "notifications/resources/list_changed":
				ResourcesChanged?.Invoke(this, server);
				break;

			case "notifications/prompts/list_changed":
				PromptsChanged?.Invoke(this, server);
				break;
		}
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var transport in _transports)
		{
			await transport.DisposeAsync().ConfigureAwait(false);
		}

		_transports.Clear();
	}

	internal void AddServer(ServerEntry server) => _servers.Add(server);

	internal void AddState(McpServerState state) => _states.Add(state);

	internal void AddResourceServer(IMcpResourceProvider provider) => _resourceServers.Add(provider);
}

/// <summary>连接 MCP 服务器并收集它们的工具。</summary>
public static class McpServers
{
	/// <summary>
	/// 连接所有启用的服务器。单个服务器启动失败不会影响其它服务器，其失败原因会记录在
	/// <see cref="McpConnection.Errors"/> 中。
	/// </summary>
	public static async Task<McpConnection> ConnectAllAsync(
		IReadOnlyList<McpServerConfig> servers,
		Action<string>? onError = null,
		CancellationToken cancellationToken = default,
		string? agentDir = null)
	{
		var transports = new List<IAsyncDisposable>();
		var connected = new List<McpConnection.ServerEntry>();
		var tools = new List<AgentTool>();
		var resourceServers = new List<IMcpResourceProvider>();
		var errors = new List<string>();
		var states = new List<McpServerState>();
		var connection = new McpConnection(transports, connected, tools, resourceServers, errors, states);

		foreach (var config in servers)
		{
			if (!config.Enabled)
			{
				continue;
			}

			if (!config.IsStdio && !config.IsHttp)
			{
				var reason = $"{config.Name}: entry has neither command nor url";
				errors.Add(reason);
				states.Add(new McpServerState(config.Name, Connected: false, 0, "entry has neither command nor url"));
				onError?.Invoke(reason);
				continue;
			}

			// 整台服务器都不可达时不连接，省下一个进程/连接。
			if (McpExposure.IsFullyHidden(config))
			{
				states.Add(new McpServerState(config.Name, Connected: false, 0, McpExposure.Hidden));
				continue;
			}

			var toolCount = 0;

			try
			{
				IMcpToolInvoker client;
				IMcpResourceProvider? resources = null;

				if (config.IsHttp)
				{
				// 声明 auth: oauth 的服务器用存储里的访问令牌（必要时自动刷新）；
				// 配置了 clientId+clientSecret 时可静默走客户端凭据授权。
				IMcpAccessTokenProvider? tokens = config.UsesOAuth
					? new McpOAuthTokenProvider(config.Url!, agentDir, config: config)
					: null;

					var http = await McpHttpClient
						.ConnectAsync(
							config.Url!,
							config.Headers,
							config.TimeoutSpan,
							cancellationToken: cancellationToken,
							tokens: tokens)
						.ConfigureAwait(false);

					http.ConfiguredServerName = config.Name;
					transports.Add(http);
					client = http;
					resources = http;

					// HTTP 走独立的 SSE 通知流；服务器拒绝（404/405）时客户端会静默降级，
					// 与 stdio 的通知路径共用同一套热刷新逻辑。
					var httpName = config.Name;
					http.NotificationReceived += (method, _) => connection.OnNotification(httpName, method);
				}
				else
				{
					var process = await McpProcess.StartAsync(config, cancellationToken).ConfigureAwait(false);
					transports.Add(process);
					client = process.Client;
					resources = process.Client;

					var serverName = config.Name;
					process.Client.NotificationReceived += (method, _) => connection.OnNotification(serverName, method);
				}

				connected.Add(new McpConnection.ServerEntry(config.Name, client, config.TimeoutSpan));

				// 服务器 initialize 的 instructions 进入命名空间说明（pi 的 describeNamespace 返回它）。
				var namespaceInstructions = client switch
				{
					McpClient stdio => stdio.Instructions,
					McpHttpClient http => http.Instructions,
					_ => null,
				};

				foreach (var tool in await client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
				{
					// hidden 的工具不可达；其余交给会话按 exposure 决定声明还是只给脚本。
					var exposure = McpExposure.Resolve(config, tool.Name);
					if (!McpExposure.IsVisible(exposure))
					{
						continue;
					}

					tools.Add(McpToolAdapter.Create(
						client,
						config.Name,
						tool,
						config.TimeoutSpan,
						exposure,
						config.Description,
						namespaceInstructions));
					toolCount++;
				}

				states.Add(new McpServerState(config.Name, Connected: true, toolCount, Error: null));

				// 只有声明了该能力的服务器才会被资源工具指向。
				if (resources is not null && resources.Capabilities.HasResources)
				{
					resourceServers.Add(resources);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw; // 调用方中止了整个连接过程
			}
			catch (Exception ex)
			{
				// 单个服务器的失败（包括它自己的请求超时）只上报，不致命。
				var reason = $"{config.Name}: {ex.Message}";
				errors.Add(reason);
				states.Add(new McpServerState(config.Name, Connected: false, 0, ex.Message));
				onError?.Invoke(reason);
			}
		}

		return connection;
	}

	/// <summary>加载配置并连接所有启用项。</summary>
	public static Task<McpConnection> ConnectConfiguredAsync(
		string cwd,
		string? agentDir = null,
		Action<string>? onError = null,
		CancellationToken cancellationToken = default,
		bool includeProject = true) =>
		ConnectAllAsync(McpConfig.Load(cwd, agentDir, includeProject), onError, cancellationToken, agentDir);
}
