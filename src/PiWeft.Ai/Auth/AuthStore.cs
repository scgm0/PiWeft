using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.Ai.Auth;

/// 读取 <c>&lt;agentDir&gt;/auth.json</c> 里的 <c>api_key</c> 凭据。
/// 凭据解析时存储值优先于环境变量（与 pi 一致）。
/// key 值可以是明文，也可以是 <c>${VAR}</c> 模板（变量取自同一凭据的 <c>env</c> 字段或进程环境）；
/// 以 <c>!</c> 开头的命令型凭据在请求时执行外部命令取 stdout（与 pi 一致：不缓存）。
public static partial class AuthStore
{
	[GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
	private static partial Regex TemplatePattern();

	private const int CommandTimeoutMs = 10_000;

	private const int CommandOutputLimit = 64 * 1024;

	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	public static void SaveApiKey(string provider, string key, string? agentDir = null) =>
		SaveApiKeyCredential(provider, key, env: null, agentDir);

	/// 写入 <c>api_key</c> 凭据。key 为空但有 env 时保存「额外字段型」凭据；
	/// 两者都为空时保存一条环境变量/外部凭据标记（与 pi 的 credential-chain 登录一致）。
	/// 默认拒绝覆盖 OAuth：调用方（桌面端）必须让用户明确确认后才传 <paramref name="overwriteOAuth"/>。
	public static void SaveApiKeyCredential(
		string provider,
		string? key,
		IReadOnlyDictionary<string, string>? env,
		string? agentDir = null,
		bool overwriteOAuth = false)
	{
		lock (AuthFileGate.Gate)
		{
			using var fileLock = AuthFileGate.LockFile(agentDir);
			var path = Path(agentDir);
			var root = ReadRootForWrite(path);
			var existing = root[provider] as JsonObject;
			if (existing is not null && !overwriteOAuth &&
				!string.Equals(Text(existing, "type"), "api_key", StringComparison.Ordinal))
			{
				throw new InvalidOperationException($"provider '{provider}' is configured with OAuth");
			}

			// 覆盖已有 api_key 时保留未知字段（pi 可能在未来版本写入新的非敏感元数据）。
			var credential = existing is not null && string.Equals(Text(existing, "type"), "api_key", StringComparison.Ordinal)
				? existing.DeepClone().AsObject()
				: new JsonObject();
			credential["type"] = "api_key";

			if (!string.IsNullOrEmpty(key))
			{
				credential["key"] = key;
			}
			else
			{
				credential.Remove("key");
			}

			var environment = new JsonObject();
			foreach (var (name, value) in env ?? new Dictionary<string, string>(StringComparer.Ordinal))
			{
				if (!string.IsNullOrEmpty(name) && value is { Length: > 0 })
				{
					environment[name] = value;
				}
			}

			if (environment.Count > 0)
			{
				credential["env"] = environment;
			}
			else
			{
				credential.Remove("env");
			}

			root[provider] = credential;
			WriteRoot(path, root);
		}
	}

	/// 删除任意类型的凭据（API Key / OAuth）。「退出登录」与「清除 API Key」都走这里：
	/// auth.json 一个 provider 只有一份凭据，按类型删会留下解释不了的半状态。
	public static bool RemoveCredential(string provider, string? agentDir = null)
	{
		lock (AuthFileGate.Gate)
		{
			using var fileLock = AuthFileGate.LockFile(agentDir);
			var path = Path(agentDir);
			if (!File.Exists(path)) return false;
			var root = ReadRootForWrite(path);
			if (!root.Remove(provider)) return false;
			WriteRoot(path, root);
			return true;
		}
	}

	private static void WriteRoot(string path, JsonObject root)
	{
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

	private static string? Text(JsonObject entry, string key) =>
		entry[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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

	/// 取某个 provider 的 API key。文件不存在、provider 没有 api_key 凭据、模板变量缺失，
	/// 或命令型凭据执行失败时返回 null（不抛异常，让调用方继续走环境变量）。
	/// 请求路径应使用 <see cref="ReadApiKeyAsync"/>，避免在异步链路里阻塞线程。
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

	public static async Task<string?> ReadApiKeyAsync(
		string provider,
		string? agentDir = null,
		CancellationToken cancellationToken = default)
	{
		var root = await ReadRootAsync(agentDir, cancellationToken).ConfigureAwait(false);
		return await ReadApiKeyFromRootAsync(root, provider, cancellationToken).ConfigureAwait(false);
	}

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

	/// 执行一条命令型凭据命令并返回修剪后的 stdout。
	/// 命令来自用户自己的 <c>auth.json</c>（用户级文件，项目配置无法写入）；
	/// 超时 10 秒、输出上限 64 KiB，超时即终止进程并视为失败。
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

			// 64 KiB 是读入上限，不是读完再检查：超限立即杀进程，避免一条命令先吃掉几百 MB 内存。
			var stdout = process.StandardOutput;
			var output = Task.Run(() => ReadCommandOutput(process, stdout));
			var error = process.StandardError;
			_ = Task.Run(() => DrainCommandError(error));
			if (!process.WaitForExit(CommandTimeoutMs))
			{
				TryKill(process);
				return null;
			}

			// 进程已退出；输出流最多再等 2 秒，防止残留子进程占着管道不放。
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

			// 进程已退出；输出流最多再等 2 秒，防止残留子进程占着管道不放。
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

			// 输出被截断即失败：截断的 key 一定是坏的。
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

	/// 一次解析同时给出可用的 key 与该凭据是否为命令型（不执行命令）。
	/// 需要执行命令取 key 的调用方应改用 <see cref="ReadApiKey"/>。
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

	/// 不含解析结果的存储快照。认证页要同时展示「有 Key / 只有 env / 命令型 / 模板变量缺失」，
	/// 不能用 <see cref="ReadApiKey"/>，它会把后三种都折叠成 null。
	public sealed record StoredApiKey(string? Key, IReadOnlyDictionary<string, string> Environment, bool HasUsableKey)
	{
		public bool HasKey => !string.IsNullOrEmpty(Key);

		public bool IsCommand => Key?.StartsWith('!') == true;

		public bool IsTemplate => Key?.Contains("${", StringComparison.Ordinal) == true;

		public bool HasEnvironment => Environment.Count > 0;

		/// 只有 <c>{"type":"api_key"}</c>：pi 的 AWS credential-chain 之类「使用外部凭据」标记。
		public bool IsAmbientMarker => !HasKey && !HasEnvironment;

		public bool IsConfigured => HasUsableKey || HasEnvironment || IsAmbientMarker;
	}

	public static StoredApiKey? ReadStoredApiKey(string provider, string? agentDir = null) =>
		ReadStoredApiKey(ReadRoot(agentDir)?[provider]);

	/// 一次读盘解析全部 api_key 凭据；认证页要展示 40+ 个供应商，逐个读会把同一份文件读 40 次。
	public static IReadOnlyDictionary<string, StoredApiKey> ReadAllStoredApiKeys(string? agentDir = null)
	{
		var result = new Dictionary<string, StoredApiKey>(StringComparer.Ordinal);
		if (ReadRoot(agentDir) is not { } root) return result;
		foreach (var (provider, node) in root)
		{
			if (ReadStoredApiKey(node) is { } credential) result[provider] = credential;
		}

		return result;
	}

	internal static StoredApiKey? ReadStoredApiKey(JsonNode? node)
	{
		if (ReadCredential(node) is not { } credential) return null;
		var (key, env) = credential;
		var usable = key.Length > 0 && (key.StartsWith('!') || ResolveTemplate(key, env) is { Length: > 0 });
		return new StoredApiKey(key, env ?? new Dictionary<string, string>(StringComparer.Ordinal), usable);
	}

	/// 凭据 env 里的值优先于进程环境（pi 的 credential-scoped env 语义）。
	public static string? ReadStoredEnvironmentValue(string provider, string name, string? agentDir = null) =>
		ReadStoredApiKey(provider, agentDir) is { } credential &&
		credential.Environment.TryGetValue(name, out var value) && value.Length > 0
			? value
			: null;

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

	/// 一次读盘列出「有可用 API Key（含命令型凭据）」的 provider。
	/// 只做模板解析与命令型判定，不执行命令；供认证状态页批量读取。
	public static IReadOnlySet<string> ConfiguredProviders(string? agentDir = null)
	{
		var providers = new HashSet<string>(StringComparer.Ordinal);
		if (ReadRoot(agentDir) is not { } root) return providers;
		foreach (var (provider, node) in root)
		{
			if (ReadCredential(node) is not { } credential) continue;
			var (key, env) = credential;
			if (key.Length == 0)
			{
				// env-only 凭据（Vertex ADC / AWS 访问密钥）也算已配置；只写 type 的
				// ambient 标记不算，否则会把"等待外部凭据"误报成已可用。
				if (env is { Count: > 0 }) providers.Add(provider);
				continue;
			}
			if (key.StartsWith('!') || ResolveTemplate(key, env) is { Length: > 0 }) providers.Add(provider);
		}
		return providers;
	}

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

	public static bool IsCommandCredential(string provider, string? agentDir = null) =>
		ReadCredential(provider, agentDir) is { } credential && credential.Key.StartsWith('!');

	/// 列出配置了命令型凭据的 provider。只解析文件不执行命令，
	/// 供启动路径（模型可用性筛选）把这类 provider 计为可用。
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
		// key 可以缺失：pi 的 credential-chain 登录会只写 {type:"api_key", env:{...}}，
		// 甚至只写 {type:"api_key"}。把它们当成「没有可解析 Key」，不当成「不是凭据」，
		// 否则认证页会把已保存的外部凭据显示成未配置。
		if (node is not JsonObject entry || entry["type"] is not JsonValue type ||
			!type.TryGetValue<string>(out var kind) || kind != "api_key") return null;
		var key = entry["key"] is JsonValue value && value.TryGetValue<string>(out var keyText) ? keyText : string.Empty;
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
