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

		var startInfo = new ProcessStartInfo
		{
			FileName = config.Command!,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in config.Arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

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

		// 持续读取 stderr，避免话多的服务器因管道写满而阻塞。
		_ = Task.Run(async () =>
		{
			try
			{
				while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
				{
					// 诊断信息有意丢弃；stdout 归 MCP 协议使用。
				}
			}
			catch (Exception)
			{
				// 进程已退出。
			}
		});

		var client = await McpClient.ConnectAsync(
			process.StandardOutput.BaseStream,
			process.StandardInput.BaseStream,
			cancellationToken).ConfigureAwait(false);

		client.ConfiguredServerName = config.Name;
		return new McpProcess(process, client, config);
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
}

/// <summary>把 MCP 工具适配成代理的工具形状。</summary>
public static class McpToolAdapter
{
	/// <summary>注册名：<c>mcp__&lt;服务器&gt;__&lt;工具&gt;</c>，其余字符替换为 <c>_</c>。</summary>
	public static string ToolName(string server, string tool) =>
		$"mcp__{Sanitize(server)}__{Sanitize(tool)}";

	internal static string Sanitize(string value)
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
	public static AgentTool Create(IMcpToolInvoker client, string server, McpTool tool, TimeSpan? timeout = null)
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
					var content = result.Content.Count > 0
						? result.Content
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
		CancellationToken cancellationToken = default)
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
					var http = await McpHttpClient
						.ConnectAsync(config.Url!, config.Headers, config.TimeoutSpan, cancellationToken: cancellationToken)
						.ConfigureAwait(false);

					http.ConfiguredServerName = config.Name;
					transports.Add(http);
					client = http;
					resources = http;
				}
				else
				{
					var process = await McpProcess.StartAsync(config, cancellationToken).ConfigureAwait(false);
					transports.Add(process);
					client = process.Client;
					resources = process.Client;

					// stdio 能收到服务器通知；HTTP 需要打开 SSE 流，本客户端暂不这样做。
					var serverName = config.Name;
					process.Client.NotificationReceived += (method, _) => connection.OnNotification(serverName, method);
				}

				connected.Add(new McpConnection.ServerEntry(config.Name, client, config.TimeoutSpan));

				foreach (var tool in await client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
				{
					// hidden 的工具不声明给模型；逐工具 toolExposure 可覆盖服务器默认。
					if (!McpExposure.IsVisible(McpExposure.Resolve(config, tool.Name)))
					{
						continue;
					}

					tools.Add(McpToolAdapter.Create(client, config.Name, tool, config.TimeoutSpan));
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
		ConnectAllAsync(McpConfig.Load(cwd, agentDir, includeProject), onError, cancellationToken);
}
