using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.Ai.Auth;

/// <summary>
/// 读取 <c>&lt;agentDir&gt;/auth.json</c> 里的 <c>api_key</c> 凭据。
/// <para>
/// 凭据解析时存储值优先于环境变量（与 pi 一致）。
/// key 值可以是明文，也可以是 <c>${VAR}</c> 模板（变量取自同一凭据的 <c>env</c> 字段或进程环境）；
/// 以 <c>!</c> 开头的命令型凭据在请求时执行外部命令取 stdout（与 pi 一致：不缓存）。
/// </para>
/// </summary>
public static partial class AuthStore
{
	[GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
	private static partial Regex TemplatePattern();

	/// <summary>命令型凭据的执行上限：超时或超长输出都视为取值失败。</summary>
	private const int CommandTimeoutMs = 10_000;

	private const int CommandOutputLimit = 64 * 1024;

	/// <summary>凭据文件的位置。</summary>
	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	/// <summary>写入一个 api_key 凭据，保留 auth.json 里的其它 provider 与字段。</summary>
	public static void SaveApiKey(string provider, string key, string? agentDir = null)
	{
		lock (AuthFileGate.Gate)
		{
			var path = Path(agentDir);
			var root = ReadRootForWrite(path);
			root[provider] = new JsonObject
			{
				["type"] = "api_key",
				["key"] = key,
			};

			var directory = System.IO.Path.GetDirectoryName(path);
			if (directory is { Length: > 0 })
			{
				Directory.CreateDirectory(directory);
			}

			var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
			File.WriteAllText(temporary, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
			{
				WriteIndented = true,
				Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			}));
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			File.Move(temporary, path, overwrite: true);
		}
	}

	/// <summary>写盘前读取；文件缺失返回空对象，损坏/不可读时抛错而不是覆盖。</summary>
	private static JsonObject ReadRootForWrite(string path)
	{
		if (!File.Exists(path)) return new JsonObject();
		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
				?? throw new CodexAuthException($"无法解析 {path}：顶层不是对象；为避免覆盖其它凭据已中止写入");
		}
		catch (System.Text.Json.JsonException exception)
		{
			throw new CodexAuthException($"无法解析 {path}（{exception.Message}）；为避免覆盖其它凭据已中止写入");
		}
	}

	/// <summary>
	/// 取某个 provider 的 API key。文件不存在、provider 没有 api_key 凭据、模板变量缺失，
	/// 或命令型凭据执行失败时返回 null（不抛异常，让调用方继续走环境变量）。
	/// <para>请求路径应使用 <see cref="ReadApiKeyAsync"/>，避免在异步链路里阻塞线程。</para>
	/// </summary>
	public static string? ReadApiKey(string provider, string? agentDir = null)
	{
		var credential = ReadCredential(provider, agentDir);
		if (credential is null)
		{
			return null;
		}

		var (key, env) = credential.Value;
		if (key.Length == 0)
		{
			return null;
		}

		// 命令型凭据（!cmd）在取值时执行，stdout 即为 key；与 pi 一致不缓存。
		if (key.StartsWith('!'))
		{
			return ExecuteCommandKey(key[1..]);
		}

		var resolved = ResolveTemplate(key, env);
		return string.IsNullOrEmpty(resolved) ? null : resolved;
	}

	/// <summary><see cref="ReadApiKey"/> 的异步版本：读盘与命令型凭据都不占用调用线程。</summary>
	public static async Task<string?> ReadApiKeyAsync(
		string provider,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		var root = await ReadRootAsync(agentDir, cancellationToken).ConfigureAwait(false);
		return await ReadApiKeyFromRootAsync(root, provider, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>从已解析的 auth.json 根节点取 key；供凭据解析链一次读盘同时处理 API Key 与 OAuth。</summary>
	internal static async Task<string?> ReadApiKeyFromRootAsync(
		JsonObject? root,
		string provider,
		CancellationToken cancellationToken)
	{
		if (ReadCredential(root?[provider]) is not { } credential)
		{
			return null;
		}

		var (key, env) = credential;
		if (key.Length == 0)
		{
			return null;
		}

		if (key.StartsWith('!'))
		{
			return await ExecuteCommandKeyAsync(key[1..], cancellationToken).ConfigureAwait(false);
		}

		var resolved = ResolveTemplate(key, env);
		return string.IsNullOrEmpty(resolved) ? null : resolved;
	}

	/// <summary>
	/// 执行一条命令型凭据命令并返回修剪后的 stdout。
	/// 命令来自用户自己的 <c>auth.json</c>（用户级文件，项目配置无法写入）；
	/// 超时 10 秒、输出上限 64 KiB，超时即终止进程并视为失败。
	/// </summary>
	public static string? ExecuteCommandKey(string command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}

		try
		{
			using var process = Process.Start(CreateCommandStartInfo(command));
			if (process is null)
			{
				return null;
			}

			// 输出上限 64 KiB 是读入上限，不是“读完再检查”：超限立即杀进程，
			// 避免一条命令先吃掉几百 MB 内存再被判定失败。
			var stdout = process.StandardOutput;
			var output = Task.Run(() => ReadCommandOutput(process, stdout));
			var error = process.StandardError;
			_ = Task.Run(() => DrainCommandError(error));
			if (!process.WaitForExit(CommandTimeoutMs))
			{
				TryKill(process);
				return null;
			}

			// 进程已退出；输出流最多再等 2 秒，防止游离子进程占着管道不放。
			try
			{
				if (!output.Wait(2_000))
				{
					TryKill(process);
					return null;
				}
			}
			catch (AggregateException)
			{
				return null;
			}

			var text = output.Result;
			if (text is null || process.ExitCode != 0)
			{
				return null;
			}

			return text.Trim('\r', '\n') is { Length: > 0 } key ? key : null;
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or ObjectDisposedException)
		{
			return null;
		}
	}

	/// <summary><see cref="ExecuteCommandKey"/> 的异步版本；调用方取消时终止进程并向上抛出。</summary>
	public static async Task<string?> ExecuteCommandKeyAsync(
		string command,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return null;
		}

		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			using var process = Process.Start(CreateCommandStartInfo(command));
			if (process is null)
			{
				return null;
			}

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(CommandTimeoutMs);

			var stdout = process.StandardOutput;
			var output = ReadCommandOutputAsync(process, stdout, timeout.Token);
			var error = process.StandardError;
			_ = DrainCommandErrorAsync(error, timeout.Token);
			try
			{
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				TryKill(process);
				cancellationToken.ThrowIfCancellationRequested();
				return null;
			}

			// 进程已退出；输出流最多再等 2 秒，防止游离子进程占着管道不放。
			(string? Text, bool Truncated) result;
			try
			{
				result = await output.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				TryKill(process);
				return null;
			}
			catch (OperationCanceledException)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return null;
			}

			// 输出截断即视为失败：被截断的 key 一定是坏的。
			if (result.Truncated || result.Text is null || process.ExitCode != 0)
			{
				return null;
			}

			return result.Text.Trim('\r', '\n') is { Length: > 0 } key ? key : null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException or ObjectDisposedException)
		{
			return null;
		}
	}

	/// <summary>读 stdout，最多读 <see cref="CommandOutputLimit"/> 个字符；超限立即杀进程并返回 null。</summary>
	private static string? ReadCommandOutput(Process process, StreamReader reader)
	{
		var buffer = ArrayPool<char>.Shared.Rent(4096);
		var builder = new StringBuilder(Math.Min(CommandOutputLimit, 4096));
		try
		{
			while (builder.Length < CommandOutputLimit)
			{
				var remaining = CommandOutputLimit - builder.Length;
				var count = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
				if (count == 0)
				{
					break;
				}

				builder.Append(buffer, 0, count);
			}

			if (builder.Length >= CommandOutputLimit)
			{
				TryKill(process);
				return null;
			}

			return builder.ToString();
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
		{
			return null;
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static async Task<(string? Text, bool Truncated)> ReadCommandOutputAsync(
		Process process,
		StreamReader reader,
		CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<char>.Shared.Rent(4096);
		var builder = new StringBuilder(Math.Min(CommandOutputLimit, 4096));
		try
		{
			while (builder.Length < CommandOutputLimit)
			{
				var remaining = CommandOutputLimit - builder.Length;
				var count = await reader
					.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
					.ConfigureAwait(false);
				if (count == 0)
				{
					break;
				}

				builder.Append(buffer, 0, count);
			}

			if (builder.Length >= CommandOutputLimit)
			{
				TryKill(process);
				return (null, true);
			}

			return (builder.ToString(), false);
		}
		catch (OperationCanceledException)
		{
			return (null, true);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
		{
			return (null, true);
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void DrainCommandError(StreamReader reader)
	{
		var buffer = ArrayPool<char>.Shared.Rent(4096);
		try
		{
			while (reader.Read(buffer, 0, buffer.Length) > 0)
			{
			}
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
		{
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static async Task DrainCommandErrorAsync(StreamReader reader, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<char>.Shared.Rent(4096);
		try
		{
			while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
			{
			}
		}
		catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
		{
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException or ObjectDisposedException)
		{
			// 进程可能已退出，或权限/平台不支持；都不影响调用方继续回落到环境变量。
		}
	}

	private static ProcessStartInfo CreateCommandStartInfo(string command)
	{
		var start = OperatingSystem.IsWindows()
			? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command}\"")
			: new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"")}\"");
		start.RedirectStandardOutput = true;
		start.RedirectStandardError = true;
		start.UseShellExecute = false;
		start.CreateNoWindow = true;
		return start;
	}

	/// <summary>
	/// 一次解析同时给出可用的 key 与该凭据是否为命令型（不执行命令）。
	/// 需要执行命令取 key 的调用方应改用 <see cref="ReadApiKey"/>。
	/// </summary>
	public static (string? Key, bool IsCommand) ReadCredentialState(string provider, string? agentDir = null)
	{
		if (ReadCredential(provider, agentDir) is not { } credential)
		{
			return (null, false);
		}

		var (key, env) = credential;
		if (key.Length == 0)
		{
			return (null, false);
		}

		if (key.StartsWith('!'))
		{
			return (null, true);
		}

		var resolved = ResolveTemplate(key, env);
		return (string.IsNullOrEmpty(resolved) ? null : resolved, false);
	}

	/// <summary><see cref="ReadCredentialState"/> 的异步版本；只解析文件，不执行命令。</summary>
	public static async Task<(string? Key, bool IsCommand)> ReadCredentialStateAsync(
		string provider,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		if (await ReadCredentialAsync(provider, agentDir, cancellationToken).ConfigureAwait(false) is not { } credential)
		{
			return (null, false);
		}

		var (key, env) = credential;
		if (key.Length == 0)
		{
			return (null, false);
		}

		if (key.StartsWith('!'))
		{
			return (null, true);
		}

		var resolved = ResolveTemplate(key, env);
		return (string.IsNullOrEmpty(resolved) ? null : resolved, false);
	}

	/// <summary>
	/// 一次读盘列出「有可用 API Key（含命令型凭据）」的 provider。
	/// 只做模板解析与命令型判定，不执行命令；供认证状态页批量读取。
	/// </summary>
	public static IReadOnlySet<string> ConfiguredProviders(string? agentDir = null)
	{
		var providers = new HashSet<string>(StringComparer.Ordinal);
		if (ReadRoot(agentDir) is not { } root) return providers;
		foreach (var (provider, node) in root)
		{
			if (ReadCredential(node) is not { } credential) continue;
			var (key, env) = credential;
			if (key.Length == 0) continue;
			if (key.StartsWith('!') || ResolveTemplate(key, env) is { Length: > 0 }) providers.Add(provider);
		}
		return providers;
	}

	/// <summary>列出 auth.json 里配置过的 provider（用于 <c>pi auth check</c> 不带参数时逐个汇报）。</summary>
	public static IReadOnlyList<string> ListProviders(string? agentDir = null)
	{
		var path = Path(agentDir);
		if (!File.Exists(path))
		{
			return [];
		}

		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root
				? [.. root.Select(static pair => pair.Key).OrderBy(static id => id, StringComparer.Ordinal)]
				: [];
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	/// <summary>凭据是否属于「需要执行命令」的类型，用于给出更准确的提示。</summary>
	public static bool IsCommandCredential(string provider, string? agentDir = null) =>
		ReadCredential(provider, agentDir) is { } credential && credential.Key.StartsWith('!');

	/// <summary>
	/// 列出配置了命令型凭据的 provider。只解析文件不执行命令，
	/// 供启动路径（模型可用性筛选）把这类 provider 计为可用。
	/// </summary>
	public static IReadOnlyList<string> CommandCredentialProviders(string? agentDir = null)
	{
		var providers = new List<string>();
		if (ReadRoot(agentDir) is not { } root) return providers;
		foreach (var (provider, node) in root)
		{
			if (ReadCredential(node) is { } credential && credential.Key.StartsWith('!'))
			{
				providers.Add(provider);
			}
		}

		return providers;
	}

	/// <summary>一次读取全部 API key；模型目录筛选无需为每个模型重复解析凭据文件。</summary>
	public static IReadOnlyDictionary<string, string> ReadApiKeys(string? agentDir = null)
	{
		var keys = new Dictionary<string, string>(StringComparer.Ordinal);
		if (ReadRoot(agentDir) is not { } root) return keys;
		foreach (var (provider, node) in root)
		{
			if (ReadCredential(node) is not { } credential || credential.Key.StartsWith('!')) continue;
			if (ResolveTemplate(credential.Key, credential.Env) is { Length: > 0 } key) keys[provider] = key;
		}
		return keys;
	}

	/// <summary>读出 <c>(key, env)</c>；没有 api_key 凭据时返回 null。</summary>
	private static (string Key, Dictionary<string, string>? Env)? ReadCredential(string provider, string? agentDir) =>
		ReadCredential(ReadRoot(agentDir)?[provider]);

	private static async Task<(string Key, Dictionary<string, string>? Env)?> ReadCredentialAsync(
		string provider,
		string? agentDir,
		CancellationToken cancellationToken) =>
		ReadCredential((await ReadRootAsync(agentDir, cancellationToken).ConfigureAwait(false))?[provider]);

	private static JsonObject? ReadRoot(string? agentDir)
	{
		try
		{
			var path = Path(agentDir);
			return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	internal static async Task<JsonObject?> ReadRootAsync(string? agentDir, CancellationToken cancellationToken)
	{
		try
		{
			var path = Path(agentDir);
			if (!File.Exists(path))
			{
				return null;
			}

			var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			return JsonNode.Parse(text) as JsonObject;
		}
		catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	internal static (string Key, Dictionary<string, string>? Env)? ReadCredential(JsonNode? node)
	{
		if (node is not JsonObject entry || entry["type"] is not JsonValue type ||
			!type.TryGetValue<string>(out var kind) || kind != "api_key" ||
			entry["key"] is not JsonValue value || !value.TryGetValue<string>(out var key)) return null;
		Dictionary<string, string>? env = null;
		if (entry["env"] is JsonObject envNode)
		{
			env = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var (name, item) in envNode)
			{
				if (item is JsonValue text && text.TryGetValue<string>(out var content)) env[name] = content;
			}
		}
		return (key, env);
	}

	/// <summary>展开 <c>${VAR}</c>：先看凭据自带的 env，再看进程环境；任一变量缺失即整体不可用（与 pi 一致）。</summary>
	private static string? ResolveTemplate(string key, Dictionary<string, string>? env)
	{
		if (!key.Contains("${", StringComparison.Ordinal))
		{
			return key;
		}

		var missing = false;
		var resolved = TemplatePattern().Replace(key, match =>
		{
			var name = match.Groups[1].Value;

			if (env is not null && env.TryGetValue(name, out var fromCredential) && fromCredential.Length > 0)
			{
				return fromCredential;
			}

			if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } fromEnvironment)
			{
				return fromEnvironment;
			}

			missing = true;
			return string.Empty;
		});

		return missing ? null : resolved;
	}
}
