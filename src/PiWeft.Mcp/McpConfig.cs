using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiWeft.Mcp;

/// <summary><c>mcp.json</c> 中的一个 MCP 服务器条目。</summary>
public sealed record McpServerConfig
{
	public string Name { get; init; } = string.Empty;

	/// <summary>stdio 服务器的可执行文件；HTTP 服务器没有该字段。</summary>
	public string? Command { get; init; }

	public IReadOnlyList<string> Arguments { get; init; } = [];

	public IReadOnlyDictionary<string, string>? Env { get; init; }

	public string? Cwd { get; init; }

	/// <summary>streamable HTTP 端点，与 <see cref="Command"/> 互斥。</summary>
	public string? Url { get; init; }

	public IReadOnlyDictionary<string, string>? Headers { get; init; }

	/// <summary>单次请求超时（秒），默认 60。</summary>
	public int? Timeout { get; init; }

	public bool Enabled { get; init; } = true;

	public string? Description { get; init; }

	/// <summary>工具暴露方式，默认 <c>direct</c>；见 <see cref="McpExposure"/>。</summary>
	public string Exposure { get; init; } = McpExposure.Default;

	/// <summary>按工具名或 <c>*</c> 通配符逐工具覆盖暴露方式；精确名优先于通配符。</summary>
	public IReadOnlyDictionary<string, string>? ToolExposure { get; init; }

	public TimeSpan TimeoutSpan => TimeSpan.FromSeconds(Timeout is > 0 ? Timeout.Value : 60);

	public bool IsStdio => Command is { Length: > 0 };

	public bool IsHttp => Url is { Length: > 0 };
}

/// <summary>一个 <c>mcp.json</c> 文件：服务器名到条目的映射。</summary>
public sealed record McpConfigFile
{
	public IReadOnlyDictionary<string, McpServerEntry>? McpServers { get; init; }
}

/// <summary>服务器条目的磁盘形状。所有字段都可选，因此支持局部覆盖。</summary>
public sealed record McpServerEntry
{
	public string? Command { get; init; }

	public IReadOnlyList<string>? Args { get; init; }

	public IReadOnlyDictionary<string, string>? Env { get; init; }

	public string? Cwd { get; init; }

	public string? Url { get; init; }

	public IReadOnlyDictionary<string, string>? Headers { get; init; }

	public int? Timeout { get; init; }

	public bool? Enabled { get; init; }

	public string? Description { get; init; }

	public string? Exposure { get; init; }

	public IReadOnlyDictionary<string, string>? ToolExposure { get; init; }
}

/// <summary>
/// 加载并合并 MCP 服务器配置。用户级来自 <c>&lt;agentDir&gt;/mcp.json</c>，项目级来自
/// <c>&lt;cwd&gt;/.pi/mcp.json</c>；同名时项目条目替换用户条目，
/// 项目里的局部条目只覆盖它显式设置的字段。
/// </summary>
public static partial class McpConfig
{
	/// <summary>名称只能包含字母、数字、<c>_</c> 与 <c>-</c>。</summary>
	public static bool IsValidName(string name) =>
		name.Length > 0 && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

	/// <summary>只有 <c>-</c> 与 <c>_</c> 不同的名称视为同一个服务器。</summary>
	public static string CanonicalName(string name) => name.Replace('-', '_');

	/// <summary>存放用户配置的目录。</summary>
	public const string ConfigDirName = ".pi";

	/// <summary>与编码代理同样的解析规则：<c>$PI_CODING_AGENT_DIR</c> 或 <c>~/.pi/agent</c>。</summary>
	public static string GetAgentDir(string? agentDir = null) =>
		agentDir ?? Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") switch
		{
			{ Length: > 0 } configured => configured,
			_ => Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				ConfigDirName,
				"agent"),
		};

	/// <summary>用户配置路径。</summary>
	public static string UserPath(string? agentDir = null) =>
		Path.Combine(GetAgentDir(agentDir), "mcp.json");

	/// <summary>项目配置路径。</summary>
	public static string ProjectPath(string cwd) =>
		Path.Combine(Path.GetFullPath(cwd), ConfigDirName, "mcp.json");

	/// <summary>加载并合并用户与项目配置。</summary>
	public static IReadOnlyList<McpServerConfig> Load(string cwd, string? agentDir = null, bool includeProject = true)
	{
		var user = ReadFile(UserPath(agentDir));
		// 不受信时只加载用户级 MCP 配置，项目里的 .pi/mcp.json 不参与。
		var project = includeProject ? ReadFile(ProjectPath(cwd)) : null;
		return Merge(user, project);
	}

	internal static McpConfigFile? ReadFile(string path)
	{
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize(File.ReadAllText(path), McpJsonContext.Default.McpConfigFile);
		}
		catch (JsonException)
		{
			return null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>合并用户与项目条目，并用环境变量展开 <c>${VAR}</c>。</summary>
	internal static IReadOnlyList<McpServerConfig> Merge(McpConfigFile? user, McpConfigFile? project)
	{
		var merged = new Dictionary<string, McpServerEntry>(StringComparer.Ordinal);
		var order = new List<string>();

		foreach (var (name, entry) in user?.McpServers ?? new Dictionary<string, McpServerEntry>())
		{
			merged[name] = entry;
			order.Add(name);
		}

		foreach (var (name, entry) in project?.McpServers ?? new Dictionary<string, McpServerEntry>())
		{
			if (merged.TryGetValue(name, out var existing))
			{
				// 项目里的局部条目只改动它设置的字段；除非它定义了一个全新的服务器。
				merged[name] = Override(existing, entry);
			}
			else
			{
				merged[name] = entry;
				order.Add(name);
			}
		}

		var servers = new List<McpServerConfig>(order.Count);
		var canonical = new HashSet<string>(StringComparer.Ordinal);

		foreach (var name in order)
		{
			if (!IsValidName(name))
			{
				continue; // 名称非法时跳过该条目，而不是让整个文件失败
			}

			if (!canonical.Add(CanonicalName(name)))
			{
				continue; // 只有 '-' 与 '_' 不同的名称属于同一个服务器
			}

			var entry = merged[name];
			servers.Add(new McpServerConfig
			{
				Name = name,
				Command = Expand(entry.Command),
				Arguments = [.. (entry.Args ?? []).Select(static argument => Expand(argument) ?? string.Empty)],
				Env = entry.Env is null
					? null
					: entry.Env.ToDictionary(static pair => pair.Key, static pair => Expand(pair.Value) ?? string.Empty),
				Cwd = Expand(entry.Cwd),
				Url = Expand(entry.Url),
				Headers = entry.Headers is null
					? null
					: entry.Headers.ToDictionary(static pair => pair.Key, static pair => Expand(pair.Value) ?? string.Empty),
				Timeout = entry.Timeout,
				Enabled = entry.Enabled ?? true,
				Description = entry.Description,
				Exposure = McpExposure.Normalize(entry.Exposure) ?? McpExposure.Default,
				ToolExposure = entry.ToolExposure is null
					? null
					: entry.ToolExposure.ToDictionary(static pair => pair.Key,
						static pair => McpExposure.Normalize(pair.Value) ?? McpExposure.Default),
			});
		}

		return servers;
	}

	private static McpServerEntry Override(McpServerEntry existing, McpServerEntry patch)
	{
		var definesServer = patch.Command is { Length: > 0 } || patch.Url is { Length: > 0 };
		if (definesServer)
		{
			return patch;
		}

		return existing with
		{
			Env = patch.Env ?? existing.Env,
			Headers = patch.Headers ?? existing.Headers,
			Cwd = patch.Cwd ?? existing.Cwd,
			Timeout = patch.Timeout ?? existing.Timeout,
			Enabled = patch.Enabled ?? existing.Enabled,
			Description = patch.Description ?? existing.Description,
			Exposure = patch.Exposure ?? existing.Exposure,
			ToolExposure = patch.ToolExposure ?? existing.ToolExposure,
		};
	}

	/// <summary>展开 <c>${VAR}</c> 与开头的 <c>~/</c>。</summary>
	internal static string? Expand(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return value;
		}

		var expanded = value;
		if (expanded.Contains("${", StringComparison.Ordinal))
		{
			expanded = EnvironmentPattern().Replace(expanded, match =>
				Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? string.Empty);
		}

		if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
		{
			expanded = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				expanded[2..]);
		}

		return expanded;
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
	private static partial System.Text.RegularExpressions.Regex EnvironmentPattern();
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	WriteIndented = true,
	ReadCommentHandling = JsonCommentHandling.Skip,
	AllowTrailingCommas = true)]
[JsonSerializable(typeof(McpConfigFile))]
internal partial class McpJsonContext : JsonSerializerContext
{
	// 保留源生成选项，只替换编码器。
	static McpJsonContext()
	{
		Default = new McpJsonContext(CreateOptions(Default));
	}

	private static JsonSerializerOptions CreateOptions(McpJsonContext defaultContext) =>
		new(defaultContext.GeneratedSerializerOptions!)
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
}
