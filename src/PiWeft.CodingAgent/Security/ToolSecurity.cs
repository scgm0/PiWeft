using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.CodingAgent.Security;

/// <summary>路径边界策略，与 PiDeck 同名同义。</summary>
public enum ToolPathPolicy
{
	/// <summary>不限制目录（仍然受 <c>denyDirs</c> 与敏感文件规则约束）。</summary>
	Unrestricted,

	/// <summary>只允许工作目录内。</summary>
	Workspace,

	/// <summary>允许工作目录加上 <see cref="ToolSecurity.CustomAllowDirs"/>。</summary>
	Custom,
}

/// <summary>
/// 工具调用安全门。在 <c>beforeToolCall</c> 钩子里按工具名与参数判定，
/// 命中即阻止本次调用并把原因交给模型 —— 与 PiDeck 的 <c>security/policy.ts</c> 同一套语义，
/// 但不依赖任何交互式确认（本端口没有「先确认再执行」的审批窗口，所以动作只有放行/拒绝两种）。
/// <para>
/// 默认等级是 <see cref="OffLevel"/>：安全门默认**完全放行**，行为与未引入它时一致，
/// 也不破坏 pi 的兼容语义。想获得保护就显式选 <see cref="StandardLevel"/> 或 <see cref="StrictLevel"/>。
/// </para>
/// <para>
/// 与 PiDeck 的已知差异（有意为之）：
/// <list type="bullet">
/// <item><description>PiDeck 的「危险命令先确认」在这里退化为「直接拒绝」，因为本端口没有审批 UI；
/// 因此默认危险命令表只收真正不可逆的破坏性命令，不收 <c>cp</c>/<c>mv</c>/<c>npm install</c>
/// 这类日常动作 —— 那些在 PiDeck 里是「问一下」，在这里直接拒绝会毁掉正常工作流。</description></item>
/// <item><description>只对内置文件工具与 shell 工具生效；MCP 与自定义工具没有可判定的路径语义，一律放行
/// （与 PiDeck 只覆盖 <c>SECURITY_TOOL_LABELS</c> 里那几个工具一致）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record ToolSecurity
{
	public const string OffLevel = "off";
	public const string StandardLevel = "standard";
	public const string StrictLevel = "strict";

	/// <summary>默认敏感路径：<c>.env</c> 家族、<c>.git</c> 元数据、私钥、包管理凭据、证书。</summary>
	/// <remarks>与 PiDeck <c>DEFAULT_SENSITIVE_PATH_PATTERNS</c> 逐条一致。</remarks>
	public static IReadOnlyList<string> SensitivePathPatterns { get; } =
	[
		@"(^|[\\/])\.env([.$]|$)",
		@"(^|[\\/])\.git([\\/]|$)",
		@"(^|[\\/])(id_rsa|id_ed25519|id_ecdsa)(\.pub)?$",
		@"(^|[\\/])\.(npmrc|yarnrc|pnpm-workspace)([.$]|$)",
		@"(\.pem|\.key|\.p12)$",
	];

	/// <summary>
	/// 不可逆的破坏性命令。刻意只收「执行完就没有回头路」的那些：
	/// 删除根/家目录、格式化、直接写块设备、关机能、提权式改根权限。
	/// 日常动作（删构建产物、装依赖、git 操作）不在其中 —— 见类型注释里的差异说明。
	/// </summary>
	public static IReadOnlyList<string> CatastrophicCommands { get; } =
	[
		// rm -rf /、rm -rf /*、rm -rf ~、rm -rf $HOME 及其常见变体。
		@"\brm\s+(-[A-Za-z-]+\s+)*(-[A-Za-z]*[rR][A-Za-z]*\s+)*(-[A-Za-z]*[fF][A-Za-z]*\s+)*(/(\*)?|~(\/\*)?|\$HOME(\/\*)?|%USERPROFILE%(\/\*)?)\s*($|[;&|])",
		@"\bmkfs(\.[A-Za-z0-9]+)?\b",
		@"\bdd\b[^|;&]*\bof\s*=\s*/dev/(sd|nvme|hd|vd|mmcblk)",
		@">\s*/dev/(sd|nvme|hd|vd|mmcblk)",
		// fork bomb。
		@":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:[^}]*\}\s*;?\s*:",
		@"\b(shutdown|reboot|halt|poweroff)\b",
		@"\bchmod\s+(-[A-Za-z]+\s+)*777\s+/\s*($|[;&|])",
		@"\bchown\s+(-[A-Za-z]+\s+)*\S+\s+/\s*($|[;&|])",
		// Windows 侧的等价动作：format 盘符、rd /s /q 根、递归强删根。
		@"\bformat\s+[A-Za-z]:",
		@"\brd\s+/s\s+/q\s+[A-Za-z]:\\?\s*($|[;&|])",
		@"\bRemove-Item\b[^|;&]*-Recurse\b[^|;&]*-Force\b[^|;&]*\b[A-Za-z]:\\\s*($|[;&|])",
	];

	/// <summary>没有任何约束，等价于安全门关闭。</summary>
	public static ToolSecurity Off { get; } = new();

	/// <summary>敏感文件受保护（读也拒绝），灾难性命令直接拒绝；目录不限制。</summary>
	public static ToolSecurity Standard { get; } = new()
	{
		Level = StandardLevel,
		ProtectSensitivePaths = true,
		DenyBashPatterns = CatastrophicCommands,
	};

	/// <summary><see cref="Standard"/> 之上再加工作目录边界：目录外的写入一律拒绝。</summary>
	public static ToolSecurity Strict { get; } = Standard with
	{
		Level = StrictLevel,
		PathPolicy = ToolPathPolicy.Workspace,
	};

	/// <summary>等级 id；<see cref="OffLevel"/> 表示安全门不生效。</summary>
	public string Level { get; init; } = OffLevel;

	/// <summary>是否保护敏感路径（读与写都拒绝）。</summary>
	public bool ProtectSensitivePaths { get; init; }

	public ToolPathPolicy PathPolicy { get; init; }

	/// <summary><see cref="ToolPathPolicy.Custom"/> 下额外放行的目录；支持 <c>~</c>。</summary>
	public IReadOnlyList<string> CustomAllowDirs { get; init; } = [];

	/// <summary>无条件禁地：读写都拒绝，也不提供绕过出口。</summary>
	public IReadOnlyList<string> DenyDirs { get; init; } = [];

	/// <summary>shell 命令的拒绝模式（正则源，忽略大小写）。</summary>
	public IReadOnlyList<string> DenyBashPatterns { get; init; } = [];

	public bool Enabled => !string.Equals(Level, OffLevel, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// 按等级取默认策略，再用显式配置覆盖。未识别的等级名按 <see cref="OffLevel"/> 处理 ——
	/// 配置写错就当安全门关闭，而不是猜一个更严格的等级把用户的工作流卡死。
	/// </summary>
	public static ToolSecurity FromLevel(
		string? level,
		bool? protectSensitivePaths = null,
		ToolPathPolicy? pathPolicy = null,
		IReadOnlyList<string>? customAllowDirs = null,
		IReadOnlyList<string>? denyDirs = null,
		IReadOnlyList<string>? denyBashPatterns = null)
	{
		var normalized = (level ?? OffLevel).Trim().ToLowerInvariant();
		var baseline = normalized switch
		{
			StandardLevel => Standard,
			StrictLevel => Strict,
			_ => Off,
		};

		return baseline with
		{
			ProtectSensitivePaths = protectSensitivePaths ?? baseline.ProtectSensitivePaths,
			PathPolicy = pathPolicy ?? baseline.PathPolicy,
			CustomAllowDirs = customAllowDirs ?? baseline.CustomAllowDirs,
			DenyDirs = denyDirs ?? baseline.DenyDirs,
			DenyBashPatterns = denyBashPatterns ?? baseline.DenyBashPatterns,
		};
	}

	/// <summary>
	/// 判定一次工具调用是否可以放行；返回 <see langword="null"/> 表示放行，否则是给模型看的中文原因。
	/// <paramref name="cwd"/> 是会话工作目录，用于路径边界与相对路径解析。
	/// </summary>
	public string? BlockingReason(string toolName, JsonObject arguments, string cwd)
	{
		if (!Enabled)
		{
			return null;
		}

		if (IsShellTool(toolName))
		{
			return ShellBlockingReason(arguments, toolName);
		}

		if (!IsPathTool(toolName, out var writes, out var pathArgument))
		{
			return null;
		}

		// 没给路径时工具按 cwd 工作，边界检查没有意义。
		if (String(arguments[pathArgument]) is not { Length: > 0 } rawPath)
		{
			return null;
		}

		string resolved;
		try
		{
			resolved = RealPath(ToolPaths.ResolveToCwd(rawPath, cwd));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			// 无法解析的路径（非法字符、超长等）交给工具自己报错，这里不抢它的错误信息。
			return null;
		}

		foreach (var denyDir in DenyDirs)
		{
			if (denyDir.Length > 0 && IsInside(resolved, ResolveDirectory(denyDir, cwd)))
			{
				return $"已被安全策略拒绝：`{rawPath}` 位于禁地 `{denyDir}`。这一条没有绕过出口，请换一个位置。";
			}
		}

		if (ProtectSensitivePaths && MatchesSensitivePath(resolved))
		{
			return $"已被安全策略拒绝：`{rawPath}` 是受保护的敏感文件（密钥 / .env / .git 元数据一类）。请让用户自己处理这个文件。";
		}

		if (!writes || PathPolicy == ToolPathPolicy.Unrestricted)
		{
			return null;
		}

		if (IsInside(resolved, RealPath(Path.GetFullPath(cwd))))
		{
			return null;
		}

		if (PathPolicy == ToolPathPolicy.Custom)
		{
			foreach (var allowDir in CustomAllowDirs)
			{
				if (allowDir.Length > 0 && IsInside(resolved, ResolveDirectory(allowDir, cwd)))
				{
					return null;
				}
			}
		}

		return $"已被安全策略拒绝：`{rawPath}` 在工作目录之外，当前等级 `{Level}` 只允许写工作目录内的文件。请让用户调整安全等级，或改在项目目录里操作。";
	}

	/// <summary>路径是否命中敏感文件规则（按文件名模式匹配完整路径的任意一段）。</summary>
	public static bool MatchesSensitivePath(string path)
	{
		var normalized = NormalizeForCompare(path);
		foreach (var pattern in SensitivePathPatterns)
		{
			try
			{
				if (Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
				{
					return true;
				}
			}
			catch (ArgumentException)
			{
				// 内置模式都是常量；真出现语法错误时跳过，不要把安全门自己搞崩。
			}
		}

		return false;
	}

	private string? ShellBlockingReason(JsonObject arguments, string toolName)
	{
		if (DenyBashPatterns.Count == 0 || String(arguments["command"]) is not { Length: > 0 } command)
		{
			return null;
		}

		foreach (var pattern in DenyBashPatterns)
		{
			if (pattern.Length == 0)
			{
				continue;
			}

			bool matched;
			try
			{
				matched = Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
			}
			catch (ArgumentException)
			{
				continue;
			}
			catch (RegexMatchTimeoutException)
			{
				// 回溯爆炸的配置正则不能变成「静默放行」，按命中处理更安全。
				matched = true;
			}

			if (matched)
			{
				return $"已被安全策略拒绝：`{toolName}` 命令匹配当前等级 `{Level}` 的危险模式 `{pattern}`。这是不可逆操作，请让用户自己决定是否执行。";
			}
		}

		return null;
	}

	private static bool IsShellTool(string toolName) =>
		string.Equals(toolName, "bash", StringComparison.Ordinal)
		|| string.Equals(toolName, "powershell", StringComparison.Ordinal);

	/// <summary>该工具是否带路径参数，以及它是否写盘。</summary>
	private static bool IsPathTool(string toolName, out bool writes, out string pathArgument)
	{
		pathArgument = "path";
		switch (toolName)
		{
			case "read":
			case "ls":
			case "find":
			case "grep":
				writes = false;
				return true;
			case "write":
			case "edit":
				writes = true;
				return true;
			default:
				writes = false;
				return false;
		}
	}

	private static string ResolveDirectory(string directory, string cwd) =>
		RealPath(ToolPaths.ResolveToCwd(directory, cwd));

	/// <summary>
	/// 解析符号链接，得到用于比较的真实路径。不存在的尾部（例如要新建的文件）按「已存在的最近祖先是真实路径」
	/// 拼回去 —— 否则 <c>link/to/new.txt</c> 会拿字面量去和真实前缀比较，边界检查被链接绕过去。
	/// </summary>
	private static string RealPath(string path) => Resolve(path, depth: 0);

	private static string Resolve(string path, int depth)
	{
		var full = Path.GetFullPath(path);
		if (depth > 32)
		{
			return full;
		}

		try
		{
			if (File.Exists(full))
			{
				var target = File.ResolveLinkTarget(full, returnFinalTarget: true);
				return target is null ? full : Path.GetFullPath(target.FullName);
			}

			if (Directory.Exists(full))
			{
				var target = Directory.ResolveLinkTarget(full, returnFinalTarget: true);
				return target is null ? full : Path.GetFullPath(target.FullName);
			}

			var parent = Path.GetDirectoryName(full);
			return string.IsNullOrEmpty(parent) ? full : Path.Combine(Resolve(parent, depth + 1), Path.GetFileName(full));
		}
		catch (IOException)
		{
			return full;
		}
		catch (UnauthorizedAccessException)
		{
			return full;
		}
	}

	private static bool IsInside(string target, string root)
	{
		var normalizedTarget = NormalizeForCompare(target);
		var normalizedRoot = NormalizeForCompare(root);
		if (normalizedRoot.Length == 0)
		{
			return true;
		}

		if (string.Equals(normalizedTarget, normalizedRoot, PathComparison))
		{
			return true;
		}

		return normalizedTarget.StartsWith(normalizedRoot + '/', PathComparison);
	}

	private static string NormalizeForCompare(string path)
	{
		var normalized = path.Replace('\\', '/').TrimEnd('/');
		return normalized.Length == 0 ? "/" : normalized;
	}

	/// <summary>Windows / macOS 的路径大小写不敏感，Linux 敏感。</summary>
	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

	private static string? String(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
			? value.GetValue<string>()
			: null;
}
