using System.Diagnostics;
using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.Mcp;

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

		// 持续读取 stderr，避免话多的服务器写满管道而阻塞；同时留一份有界摘要。
		// 进程异常退出时，挂起请求能带上退出码与最后几行 stderr，不只一句
		// "connection closed"（见 McpClient.ClosureDiagnostics）。
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
			// 握手失败时尽量带上退出码与 stderr 摘要：正在崩溃的进程几乎都已退出，短等一拍即可；
			// 还活着的服务器按失败原因处理（超时/协议错误），杀掉后不猜退出码。
			var detail = await ExitDetailAsync(process, stderrTail, wait: TimeSpan.FromMilliseconds(500))
				.ConfigureAwait(false);

			// 握手失败要杀掉已拉起的进程，否则它一直挂着，占住内存与管道。
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

	/// 汇总服务器进程的退出诊断：退出码与 stderr 尾部。先给正在退出的进程一小段时间，
	/// 超时按仍存活处理；两者都拿不到就返回空串，调用方沿用原始错误。
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

	private sealed class StderrTail(int capacity)
	{
		private const int MaxLineLength = 500;

		private readonly Lock _gate = new();
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

public static class McpToolAdapter
{
	public static string ToolName(string server, string tool) =>
		$"mcp__{Sanitize(server)}__{Sanitize(tool)}";

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

public sealed record McpServerState(string Name, bool Connected, int ToolCount, string? Error);

/// 一台服务器连接的结果，后台连接时按配置顺序摊进 <see cref="McpConnection"/>。
internal sealed record ServerConnectResult(
	IReadOnlyList<IAsyncDisposable> Transports,
	McpConnection.ServerEntry? Entry,
	IReadOnlyList<AgentTool> Tools,
	IMcpResourceProvider? ResourceServer,
	McpServerState? State,
	string? Error);

public sealed class McpConnection : IAsyncDisposable
{
	private readonly List<IAsyncDisposable> _transports;
	private readonly List<ServerEntry> _servers;
	private readonly List<AgentTool> _tools;
	private readonly List<IMcpResourceProvider> _resourceServers;
	private readonly List<string> _errors;
	private readonly List<McpServerState> _states;
	private readonly SemaphoreSlim _refreshGate = new(1, 1);
	private readonly McpServerLog? _log;

	// 后台连接的进度：结果按配置下标就位，全部完成后按顺序摊进各列表。
	private readonly Lock _fillGate = new();
	private readonly Dictionary<int, ServerConnectResult?> _fillResults = [];
	private readonly List<int> _directPending = [];
	private readonly List<string> _pendingErrors = [];
	private TaskCompletionSource _directReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private TaskCompletionSource _fillComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task? _fill;
	private bool _filling;
	private int _nextFillIndex;

	internal McpConnection(
		List<IAsyncDisposable> transports,
		List<ServerEntry> servers,
		List<AgentTool> tools,
		List<IMcpResourceProvider> resourceServers,
		List<string> errors,
		List<McpServerState> states,
		McpServerLog? log = null)
	{
		_transports = transports;
		_servers = servers;
		_tools = tools;
		_resourceServers = resourceServers;
		_errors = errors;
		_states = states;
		_log = log;
	}

	internal sealed record ServerEntry(string Name, IMcpToolInvoker Client, TimeSpan Timeout);

	/// 所有已连接服务器的工具，已加命名空间。后台连接与 <c>tools/list_changed</c> 会在另一个线程上
	/// 改写这些列表，因此读到的是一份快照：直接返回内部列表会让调用方在枚举时撞上
	/// "Collection was modified"。
	public IReadOnlyList<AgentTool> Tools
	{
		get
		{
			lock (_fillGate)
			{
				return [.. _tools];
			}
		}
	}

	public IReadOnlyList<IMcpResourceProvider> ResourceServers
	{
		get
		{
			lock (_fillGate)
			{
				return [.. _resourceServers];
			}
		}
	}

	public IReadOnlyList<McpServerState> Servers
	{
		get
		{
			lock (_fillGate)
			{
				return [.. _states];
			}
		}
	}

	public IReadOnlyList<string> Errors
	{
		get
		{
			lock (_fillGate)
			{
				return [.. _errors];
			}
		}
	}

	public event Action<McpConnection>? ToolsChanged;

	public event Action<McpConnection, string>? ResourcesChanged;

	public event Action<McpConnection, string>? PromptsChanged;

	/// 后台连接是否还在进行。
	public bool IsConnecting
	{
		get
		{
			lock (_fillGate)
			{
				return _filling;
			}
		}
	}

	/// 是否还有 direct 工具在连接：首轮请求只在它为 true 时等待。
	public bool HasPendingDirectTools
	{
		get
		{
			lock (_fillGate)
			{
				return _directPending.Count > 0;
			}
		}
	}

	/// 等 direct 工具连上（pi 的首轮等待，默认 10 秒）。没有 direct 服务器时立刻返回。
	public async Task WaitForDirectToolsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		Task ready;
		lock (_fillGate)
		{
			if (_directPending.Count == 0)
			{
				return;
			}

			ready = _directReady.Task;
		}

		try
		{
			await ready.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// 超时就用已经连上的工具：没连上的服务器按未声明处理。
		}
	}

	/// 等所有服务器连接结束（codemode 脚本、tool_search、资源工具需要全量工具时用）。
	public async Task WaitForAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		Task? fill;
		lock (_fillGate)
		{
			fill = _fill;
		}

		if (fill is null)
		{
			return;
		}

		try
		{
			await fill.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
		}
	}

	/// 记录需要为 direct 工具等待的服务器下标（后台连接启动前由 <see cref="McpServers"/> 设置）。
	internal void ExpectDirectTools(int index)
	{
		lock (_fillGate)
		{
			_directPending.Add(index);
		}
	}

	internal void TrackFill(Task fill)
	{
		lock (_fillGate)
		{
			_fill = fill;
			_filling = !fill.IsCompleted;
		}
	}

	/// 一台服务器连完：按配置顺序摊进各列表，并通知宿主刷新工具。
	internal void Merge(int index, ServerConnectResult? result)
	{
		List<McpServerState>? states;
		List<string>? errors;
		lock (_fillGate)
		{
			_fillResults[index] = result;
			_directPending.Remove(index);
			if (_directPending.Count == 0)
			{
				_directReady.TrySetResult();
			}

			(states, errors) = ApplyInOrder();
			if (states is { Count: > 0 })
			{
				_states.AddRange(states);
			}

			if (errors is { Count: > 0 })
			{
				_errors.AddRange(errors);
			}
		}

		if (result is not null)
		{
			ToolsChanged?.Invoke(this);
		}
	}

	internal void CompleteFill()
	{
		lock (_fillGate)
		{
			// 取消或异常会让结果停在不连续的下标上；收尾时按顺序补摊，否则它们的传输不会被释放。
			var (states, errors) = ApplyInOrder();
			while (_fillResults.Count > 0)
			{
				_nextFillIndex = _fillResults.Keys.Min();
				var (moreStates, moreErrors) = ApplyInOrder();
				states ??= moreStates;
				errors ??= moreErrors;
				if (moreStates is { Count: > 0 })
				{
					(states ??= []).AddRange(moreStates);
				}

				if (moreErrors is { Count: > 0 })
				{
					(errors ??= []).AddRange(moreErrors);
				}
			}

			if (states is { Count: > 0 })
			{
				_states.AddRange(states);
			}

			if (errors is { Count: > 0 })
			{
				_errors.AddRange(errors);
			}

			_filling = false;
			_directPending.Clear();
			_directReady.TrySetResult();
			_fillComplete.TrySetResult();
		}
	}

	/// 把已经就位的连续前缀摊进列表，保持与配置一致的顺序。
	/// 返回本次新增的状态与错误；调用方在锁外写入公开列表。
	private (List<McpServerState>? States, List<string>? Errors) ApplyInOrder()
	{
		List<McpServerState>? states = null;
		List<string>? errors = null;
		while (_fillResults.TryGetValue(_nextFillIndex, out var result))
		{
			_fillResults.Remove(_nextFillIndex);
			_nextFillIndex++;
			if (result is null)
			{
				continue;
			}

			_transports.AddRange(result.Transports);
			if (result.Entry is { } entry)
			{
				_servers.Add(entry);
			}
			_tools.AddRange(result.Tools);
			if (result.ResourceServer is { } resources)
			{
				_resourceServers.Add(resources);
			}
			if (result.State is { } state)
			{
				(states ??= []).Add(state);
			}
			if (result.Error is { } error)
			{
				(errors ??= []).Add(error);
			}
		}

		return (states, errors);
	}

	public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
	{
		await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var next = new List<AgentTool>();
			// 后台连接可能正在往 _servers 里追加：先取快照再逐个列举。
			ServerEntry[] servers;
			lock (_fillGate)
			{
				servers = [.. _servers];
			}

			foreach (var server in servers)
			{
				foreach (var tool in await server.Client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
				{
					next.Add(McpToolAdapter.Create(server.Client, server.Name, tool, server.Timeout));
				}
			}

			lock (_fillGate)
			{
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

	internal void OnNotification(string server, string method, System.Text.Json.Nodes.JsonObject? parameters = null)
	{
		switch (method)
		{
			case "notifications/message":
				_log?.Write(server, parameters);
				break;

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
		// 后台连接可能还在跑：先等它收尾（上限 5 秒），否则会漏掉刚连上的进程与连接。
		Task? fill;
		lock (_fillGate)
		{
			fill = _fill;
		}

		if (fill is { IsCompleted: false })
		{
			try
			{
				await fill.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			}
			catch (Exception)
			{
				// 连接失败或超时：已经建立的传输仍然要释放。
			}
		}

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

public static class McpServers
{
	/// 连接所有启用的服务器。单个服务器启动失败不会影响其它服务器，其失败原因会记录在
	/// <see cref="McpConnection.Errors"/> 中。
	public static Task<McpConnection> ConnectAllAsync(
		IReadOnlyList<McpServerConfig> servers,
		Action<string>? onError = null,
		CancellationToken cancellationToken = default,
		string? agentDir = null) =>
		ConnectAllAsync(servers, onError, cancellationToken, agentDir, background: false);

	/// <param name="background">为 <see langword="true"/> 时立刻返回连接对象，服务器在后台连上后
	/// 就地填充工具与状态，并通过 <see cref="McpConnection.ToolsChanged"/> 通知宿主。
	/// 首轮请求用 <see cref="McpConnection.WaitForDirectToolsAsync"/> 只为 direct 工具等连接。</param>
	public static async Task<McpConnection> ConnectAllAsync(
		IReadOnlyList<McpServerConfig> servers,
		Action<string>? onError,
		CancellationToken cancellationToken,
		string? agentDir,
		bool background)
	{
		var transports = new List<IAsyncDisposable>();
		var connected = new List<McpConnection.ServerEntry>();
		var tools = new List<AgentTool>();
		var resourceServers = new List<IMcpResourceProvider>();
		var errors = new List<string>();
		var states = new List<McpServerState>();
		var connection = new McpConnection(
			transports,
			connected,
			tools,
			resourceServers,
			errors,
			states,
			new McpServerLog(agentDir));

		for (var index = 0; index < servers.Count; index++)
		{
			if (servers[index].Enabled && McpExposure.HasDirectTools(servers[index]))
			{
				connection.ExpectDirectTools(index);
			}
		}

		var fill = FillAsync(connection, servers, onError, agentDir, cancellationToken);
		connection.TrackFill(fill);
		if (background)
		{
			return connection;
		}

		await fill.ConfigureAwait(false);
		return connection;
	}

	/// 各服务器并行连接：stdio 要起进程、HTTP 要握手，串行时 N 台服务器的启动时间相加。
	/// 后台模式下每台连上就地合并并通知，宿主不必等全部完成。
	private static async Task FillAsync(
		McpConnection connection,
		IReadOnlyList<McpServerConfig> servers,
		Action<string>? onError,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		try
		{
			await Parallel.ForEachAsync(
				Enumerable.Range(0, servers.Count),
				new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
				async (index, token) =>
				{
					var config = servers[index];
					ServerConnectResult? result;
					try
					{
						result = await ConnectOneAsync(config, connection, agentDir, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested)
					{
						throw;
					}
					catch (Exception exception)
					{
						result = new ServerConnectResult(
							[],
							null,
							[],
							null,
							new McpServerState(config.Name, Connected: false, 0, exception.Message),
							$"{config.Name}: {exception.Message}");
					}

					connection.Merge(index, result);
					if (result?.Error is { } error)
					{
						onError?.Invoke(error);
					}
				}).ConfigureAwait(false);
		}
		finally
		{
			connection.CompleteFill();
		}
	}


	private static async Task<ServerConnectResult?> ConnectOneAsync(
		McpServerConfig config,
		McpConnection connection,
		string? agentDir,
		CancellationToken cancellationToken)
	{
		if (!config.Enabled)
		{
			return null;
		}

		if (!config.IsStdio && !config.IsHttp)
		{
			return new ServerConnectResult(
				[],
				null,
				[],
				null,
				new McpServerState(config.Name, Connected: false, 0, "entry has neither command nor url"),
				$"{config.Name}: entry has neither command nor url");
		}

		// 整台服务器都不可达时不连接，省下一个进程/连接。
		if (McpExposure.IsFullyHidden(config))
		{
			return new ServerConnectResult(
				[],
				null,
				[],
				null,
				new McpServerState(config.Name, Connected: false, 0, McpExposure.Hidden),
				null);
		}

		var transports = new List<IAsyncDisposable>(1);
		try
		{
			IMcpToolInvoker client;
			IMcpResourceProvider? resources;

			if (config.IsHttp)
			{
				// 声明 auth: oauth 的服务器用存储里的访问令牌（必要时自动刷新）；
				// 配置了 clientId+clientSecret 时可静默走客户端凭据授权。
				IMcpAccessTokenProvider? tokens = config.UsesOAuth
					? new McpOAuthTokenProvider(config.Url!, agentDir, config: config, serverName: config.Name)
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
				http.NotificationReceived += (method, payload) => connection.OnNotification(httpName, method, payload);
			}
			else
			{
				var process = await McpProcess.StartAsync(config, cancellationToken).ConfigureAwait(false);
				transports.Add(process);
				client = process.Client;
				resources = process.Client;

				var serverName = config.Name;
				process.Client.NotificationReceived += (method, payload) => connection.OnNotification(serverName, method, payload);
			}

			// 服务器 initialize 的 instructions 进入命名空间说明（pi 的 describeNamespace 返回它）。
			var namespaceInstructions = client switch
			{
				McpClient stdio => stdio.Instructions,
				McpHttpClient http => http.Instructions,
				_ => null,
			};

			var tools = new List<AgentTool>();
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
			}

			return new ServerConnectResult(
				transports,
				new McpConnection.ServerEntry(config.Name, client, config.TimeoutSpan),
				tools,
				// 只有声明了该能力的服务器才会被资源工具指向。
				resources is not null && resources.Capabilities.HasResources ? resources : null,
				new McpServerState(config.Name, Connected: true, tools.Count, Error: null),
				null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			foreach (var transport in transports)
			{
				await transport.DisposeAsync().ConfigureAwait(false);
			}

			throw; // 调用方中止了整个连接过程
		}
		catch (Exception ex)
		{
			foreach (var transport in transports)
			{
				await transport.DisposeAsync().ConfigureAwait(false);
			}

			// 单个服务器的失败（包括它自己的请求超时）只上报，不致命。
			return new ServerConnectResult(
				[],
				null,
				[],
				null,
				new McpServerState(config.Name, Connected: false, 0, ex.Message),
				$"{config.Name}: {ex.Message}");
		}
	}

	public static Task<McpConnection> ConnectConfiguredAsync(
		string cwd,
		string? agentDir = null,
		Action<string>? onError = null,
		CancellationToken cancellationToken = default,
		bool includeProject = true) =>
		ConnectAllAsync(McpConfig.Load(cwd, agentDir, includeProject), onError, cancellationToken, agentDir);

	/// 启动后立刻返回，服务器在后台连接；首轮请求用 <see cref="McpConnection.WaitForDirectToolsAsync"/> 等 direct 工具。
	public static Task<McpConnection> ConnectConfiguredInBackgroundAsync(
		string cwd,
		string? agentDir = null,
		Action<string>? onError = null,
		CancellationToken cancellationToken = default,
		bool includeProject = true) =>
		ConnectAllAsync(McpConfig.Load(cwd, agentDir, includeProject), onError, cancellationToken, agentDir, background: true);
}
