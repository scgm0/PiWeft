using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiWeft.Ai.Auth;

/// <summary>
/// 读取 <c>&lt;agentDir&gt;/auth.json</c> 里的 <c>api_key</c> 凭据。
/// <para>
/// 凭据解析时存储值优先于环境变量（与 pi 一致）。
/// key 值可以是明文，也可以是 <c>${VAR}</c> 模板（变量取自同一凭据的 <c>env</c> 字段或进程环境）；
/// 以 <c>!</c> 开头的命令型凭据需要执行外部命令，本端口不执行，读取时视为不可用并给出提示。
/// </para>
/// </summary>
public static class AuthStore
{
	private static readonly Regex TemplatePattern = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

	/// <summary>凭据文件的位置。</summary>
	public static string Path(string? agentDir = null) =>
		System.IO.Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "auth.json");

	/// <summary>
	/// 取某个 provider 的 API key。文件不存在、provider 没有 api_key 凭据、模板变量缺失，
	/// 或凭据是命令型时返回 null（不抛异常，让调用方继续走环境变量）。
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

		// 命令型凭据（!cmd）需要执行外部命令取值，本端口不执行。
		if (key.StartsWith('!'))
		{
			return null;
		}

		var resolved = ResolveTemplate(key, env);
		return string.IsNullOrEmpty(resolved) ? null : resolved;
	}

	/// <summary>
	/// 一次解析同时给出可用的 key 与该凭据是否为命令型。provider 每次请求都要做这个判断，
	/// 分开调用会让 <c>auth.json</c> 被读两遍（这也是桌面/CLI 启动热路径）。
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
	private static (string Key, Dictionary<string, string> Env)? ReadCredential(string provider, string? agentDir) =>
		ReadCredential(ReadRoot(agentDir)?[provider]);

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

	private static (string Key, Dictionary<string, string> Env)? ReadCredential(JsonNode? node)
	{
		if (node is not JsonObject entry || entry["type"] is not JsonValue type ||
			!type.TryGetValue<string>(out var kind) || kind != "api_key" ||
			entry["key"] is not JsonValue value || !value.TryGetValue<string>(out var key)) return null;
		var env = new Dictionary<string, string>(StringComparer.Ordinal);
		if (entry["env"] is JsonObject envNode)
		{
			foreach (var (name, item) in envNode)
			{
				if (item is JsonValue text && text.TryGetValue<string>(out var content)) env[name] = content;
			}
		}
		return (key, env);
	}

	/// <summary>展开 <c>${VAR}</c>：先看凭据自带的 env，再看进程环境；任一变量缺失即整体不可用（与 pi 一致）。</summary>
	private static string? ResolveTemplate(string key, Dictionary<string, string> env)
	{
		if (!key.Contains("${", StringComparison.Ordinal))
		{
			return key;
		}

		var missing = false;
		var resolved = TemplatePattern.Replace(key, match =>
		{
			var name = match.Groups[1].Value;

			if (env.TryGetValue(name, out var fromCredential) && fromCredential.Length > 0)
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
