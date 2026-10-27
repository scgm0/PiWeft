using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiWeft.CodingAgent.Tools;

namespace PiWeft.CodingAgent.Security;

/// <summary>安全门对一次工具调用的裁定。</summary>
public enum SecurityEffect
{
	/// <summary>直接放行。</summary>
	Allow,

	/// <summary>需要用户确认后才能执行。</summary>
	Ask,

	/// <summary>拒绝，并把原因交给模型。</summary>
	Deny,
}

/// <summary>一次工具调用的裁定结果。<see cref="Reason"/> 只在非 Allow 时有意义。</summary>
public readonly record struct SecurityDecision(SecurityEffect Effect, string? Reason = null)
{
	public static SecurityDecision Allow { get; } = new(SecurityEffect.Allow);

	public bool IsDenied => Effect == SecurityEffect.Deny;
}

/// <summary>工具动作取值；与 PiDeck 的 <c>SecurityAction</c> 同名同义。</summary>
public static class ToolAction
{
	public const string Allow = "allow";
	public const string Ask = "ask";
	public const string Deny = "deny";

	/// <summary>非法值退回 allow：档位配置写错时宁可放行，也不要把用户的工作流卡死。</summary>
	public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		Ask => Ask,
		Deny => Deny,
		_ => Allow,
	};
}

/// <summary>
/// 一个安全档位的策略。在 <c>beforeToolCall</c> 钩子里按工具名与参数判定 —— 与 PiDeck 的
/// <c>security/policy.ts</c> 同一套语义（工具动作、路径边界、敏感文件、危险命令表、禁地）。
/// <para>
/// 与 PiDeck 的一处差异是有意为之：PiDeck 的 <c>ask</c> 依赖它的确认弹窗；本端口也有确认通道
/// （<c>IUiHost</c>），但**没有宿主时 ask 退化为拒绝**（无审批人在场却放行等于没有这道门）。
/// </para>
/// </summary>
public sealed record ToolSecurity
{
	public const string OffLevel = "off";
	public const string StandardLevel = "standard";
	public const string StrictLevel = "strict";

	/// 默认敏感路径：<c>.env</c> 家族、<c>.git</c> 元数据、私钥、包管理凭据、证书。
	/// <para>
	/// 这两张模式表必须声明在 <see cref="Standard"/> / <see cref="Strict"/> / <see cref="Builtins"/>
	/// **之前**：静态属性按文本顺序初始化，顺序反了档位拿到的就是 <see langword="null"/>，
	/// 表现为「危险命令表静默失效」而不是编译错误（有过先例，故地复发过一次）。
	/// </para>
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
	/// 不可逆的破坏性命令。刻意只收「执行完就没有回头路」的那些：删除根/家目录、格式化、
	/// 直接写块设备、fork bomb、关机、提权式改根权限、Windows 的 format / rd 根目录。
	/// <c>cp</c>/<c>mv</c>/<c>npm install</c>/<c>git commit</c> 这类日常动作不在其中 ——
	/// 它们在 PiDeck 里是「问一下」，本端口在没有审批人时把 ask 退化为拒绝，收进来会毁掉正常工作流。
	/// </summary>
	public static IReadOnlyList<string> CatastrophicCommands { get; } =
	[
		@"\brm\s+(-[A-Za-z-]+\s+)*(-[A-Za-z]*[rR][A-Za-z]*\s+)*(-[A-Za-z]*[fF][A-Za-z]*\s+)*(/(\*)?|~(\/\*)?|\$HOME(\/\*)?|%USERPROFILE%(\/\*)?)\s*($|[;&|])",
		@"\bmkfs(\.[A-Za-z0-9]+)?\b",
		@"\bdd\b[^|;&]*\bof\s*=\s*/dev/(sd|nvme|hd|vd|mmcblk)",
		@">\s*/dev/(sd|nvme|hd|vd|mmcblk)",
		@":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:[^}]*\}\s*;?\s*:",
		@"\b(shutdown|reboot|halt|poweroff)\b",
		@"\bchmod\s+(-[A-Za-z]+\s+)*777\s+/\s*($|[;&|])",
		@"\bchown\s+(-[A-Za-z]+\s+)*\S+\s+/\s*($|[;&|])",
		@"\bformat\s+[A-Za-z]:",
		@"\brd\s+/s\s+/q\s+[A-Za-z]:\\?\s*($|[;&|])",
		@"\bRemove-Item\b[^|;&]*-Recurse\b[^|;&]*-Force\b[^|;&]*\b[A-Za-z]:\\\s*($|[;&|])",
	];

	/// <summary>没有任何约束，等价于安全门关闭。</summary>
	public static ToolSecurity Off { get; } = new() { Name = "关闭" };

	/// <summary>敏感文件受保护（读也拒绝）；危险命令直接拒绝；目录不限制。</summary>
	public static ToolSecurity Standard { get; } = new()
	{
		Level = StandardLevel,
		Name = "标准",
		Description = "敏感文件受保护，灾难性命令直接拒绝；目录不限制。",
		ProtectSensitivePaths = true,
		DenyBashPatterns = CatastrophicCommands,
	};

	/// <summary><see cref="Standard"/> 之上再加工作目录边界：目录外的写入一律拒绝。</summary>
	public static ToolSecurity Strict { get; } = Standard with
	{
		Level = StrictLevel,
		Name = "严格",
		Description = "标准档 + 写入仅限工作目录；越界写入一律拒绝。",
		PathPolicy = ToolPathPolicy.Workspace,
	};

	/// <summary>内建三档；用户自定义档位追加在后面。</summary>
	public static IReadOnlyList<ToolSecurity> Builtins { get; } = [Off, Standard, Strict];


	/// <summary>档位 id（<c>off</c> / <c>standard</c> / <c>strict</c> 或自定义 id）。</summary>
	public string Level { get; init; } = OffLevel;

	/// <summary>界面显示名。</summary>
	public string Name { get; init; } = "关闭";

	public string Description { get; init; } = "完全放行所有工具调用，等同于未启用安全管理。";

	/// <summary>内建档不可删除；自定义档可以。</summary>
	public bool Builtin { get; init; } = true;

	/// <summary>是否保护敏感路径（读与写都拒绝）。</summary>
	public bool ProtectSensitivePaths { get; init; }

	public ToolPathPolicy PathPolicy { get; init; }

	/// <summary><see cref="ToolPathPolicy.Custom"/> 下额外放行的目录；支持 <c>~</c>。</summary>
	public IReadOnlyList<string> CustomAllowDirs { get; init; } = [];

	/// <summary>无条件禁地：读写都拒绝，也不提供绕过出口。</summary>
	public IReadOnlyList<string> DenyDirs { get; init; } = [];

	/// <summary>shell 命令的拒绝模式（正则源，忽略大小写）。</summary>
	public IReadOnlyList<string> DenyBashPatterns { get; init; } = [];

	/// <summary>
	/// 按工具名的动作（<c>allow</c>/<c>ask</c>/<c>deny</c>）。没有列出的工具走
	/// <see cref="DefaultAction"/>；路径边界与危险命令的判定独立于此，任何动作下都生效。
	/// </summary>
	public IReadOnlyDictionary<string, string> ToolActions { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>未在 <see cref="ToolActions"/> 中列出的工具（含 MCP 与自定义工具）的兜底动作。</summary>
	public string DefaultAction { get; init; } = ToolAction.Allow;

	/// <summary>本档是否会让任何调用被拦下。用于界面判断「这档等于没开」。</summary>
	public bool Effective =>
		ProtectSensitivePaths ||
		PathPolicy != ToolPathPolicy.Unrestricted ||
		DenyDirs.Count > 0 ||
		DenyBashPatterns.Count > 0 ||
		ToolActions.Values.Any(static action => action != ToolAction.Allow) ||
		DefaultAction != ToolAction.Allow;

	/// <summary>工具名候选集（界面按这个顺序列动作矩阵）。与 PiDeck 的 <c>SECURITY_TOOL_LABELS</c> 同集合。</summary>
	public static IReadOnlyList<string> ManagedTools { get; } =
		["read", "write", "edit", "bash", "grep", "find", "ls", "ask_question"];

	/// <summary>工具动作矩阵的中文名。</summary>
	public static string LabelOf(string tool) => tool switch
	{
		"read" => "读取文件",
		"write" => "写入文件",
		"edit" => "编辑文件",
		"bash" => "执行命令",
		"powershell" => "执行 PowerShell",
		"grep" => "搜索内容",
		"find" => "查找文件",
		"ls" => "列目录",
		"ask_question" => "提问用户",
		_ => tool,
	};

	/// <summary>按档位名取内建策略，再套用显式覆盖。未识别的档位名按 <see cref="OffLevel"/> 处理。</summary>
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
		} with
		{
			CustomAllowDirs = customAllowDirs ?? baseline.CustomAllowDirs,
			DenyDirs = denyDirs ?? baseline.DenyDirs,
			DenyBashPatterns = denyBashPatterns ?? baseline.DenyBashPatterns,
		};
	}

	/// <summary>
	/// 判定一次工具调用。<see cref="SecurityEffect.Allow"/> 时 <see cref="SecurityDecision.Reason"/> 为
	/// <see langword="null"/>；Ask 给出要向用户展示的说明。
	/// <paramref name="cwd"/> 是会话工作目录，用于路径边界与相对路径解析。
	/// </summary>
	public SecurityDecision Evaluate(string toolName, JsonObject arguments, string cwd)
	{
		// 1. 工具动作（allow/ask/deny）：显式 deny 直接结束；ask 记下但仍要继续查路径与命令 ——
		//    「要问一下」不等于「不用检查是否越界/是否灾难性」。
		var action = ActionOf(toolName);
		string? askReason = action == ToolAction.Ask
			? $"安全档位 `{Name}` 要求在执行 `{toolName}` 前确认。"
			: null;
		if (action == ToolAction.Deny)
		{
			return new SecurityDecision(SecurityEffect.Deny, $"已被安全策略拒绝：当前档位 `{Name}` 禁止使用 `{toolName}`。");
		}

		// 2. shell 命令的危险模式：优先级高于任何 ask（不可逆操作不该靠一次回车放行）。
		if (IsShellTool(toolName) && ShellBlockingReason(arguments, toolName) is { } shellReason)
		{
			return new SecurityDecision(SecurityEffect.Deny, shellReason);
		}

		// 3. 路径边界与敏感文件。MCP / 自定义工具没有可判定的路径语义，落到 ask 上仍然只是确认。
		if (!IsPathTool(toolName, out var writes, out var pathArgument))
		{
			return askReason is null
				? SecurityDecision.Allow
				: new SecurityDecision(SecurityEffect.Ask, askReason);
		}

		// 没给路径时工具按 cwd 工作，边界检查没有意义。
		if (JsonArgs(arguments, pathArgument) is not { Length: > 0 } rawPath)
		{
			return askReason is null
				? SecurityDecision.Allow
				: new SecurityDecision(SecurityEffect.Ask, askReason);
		}

		string resolved;
		try
		{
			resolved = RealPath(ToolPaths.ResolveToCwd(rawPath, cwd));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			// 无法解析的路径（非法字符、超长等）交给工具自己报错，这里不抢它的错误信息。
			return askReason is null
				? SecurityDecision.Allow
				: new SecurityDecision(SecurityEffect.Ask, askReason);
		}

		foreach (var denyDir in DenyDirs)
		{
			if (denyDir.Length > 0 && IsInside(resolved, ResolveDirectory(denyDir, cwd)))
			{
				return new SecurityDecision(SecurityEffect.Deny,
					$"已被安全策略拒绝：`{rawPath}` 位于禁地 `{denyDir}`。这一条没有绕过出口，请换一个位置。");
			}
		}

		if (ProtectSensitivePaths && MatchesSensitivePath(resolved))
		{
			return new SecurityDecision(SecurityEffect.Deny,
				$"已被安全策略拒绝：`{rawPath}` 是受保护的敏感文件（密钥 / .env / .git 元数据一类）。请让用户自己处理这个文件。");
		}

		if (writes && PathPolicy != ToolPathPolicy.Unrestricted && !InsideBoundary(resolved, cwd))
		{
			return new SecurityDecision(SecurityEffect.Deny,
				$"已被安全策略拒绝：`{rawPath}` 在工作目录之外，当前档位 `{Name}` 只允许写工作目录内的文件。请让用户调整安全档位，或改在项目目录里操作。");
		}

		return askReason is null
			? SecurityDecision.Allow
			: new SecurityDecision(SecurityEffect.Ask, askReason);
	}

	/// <summary>
	/// 只判「是否被拒绝」的便捷入口（无需审批的调用方用）。Ask 不在此列 —— 它不是拒绝。
	/// 返回 <see langword="null"/> 表示可以执行。
	/// </summary>
	public string? BlockingReason(string toolName, JsonObject arguments, string cwd) =>
		Evaluate(toolName, arguments, cwd) is { Effect: SecurityEffect.Deny } denied ? denied.Reason : null;

	/// <summary>某个工具在本档位的动作。</summary>
	public string ActionOf(string toolName) =>
		ToolActions.TryGetValue(toolName, out var action)
			? ToolAction.Normalize(action)
			: ToolAction.Normalize(DefaultAction);

	private bool InsideBoundary(string resolved, string cwd)
	{
		if (IsInside(resolved, RealPath(Path.GetFullPath(cwd))))
		{
			return true;
		}

		if (PathPolicy != ToolPathPolicy.Custom)
		{
			return false;
		}

		foreach (var allowDir in CustomAllowDirs)
		{
			if (allowDir.Length > 0 && IsInside(resolved, ResolveDirectory(allowDir, cwd)))
			{
				return true;
			}
		}

		return false;
	}

	private string? ShellBlockingReason(JsonObject arguments, string toolName)
	{
		if (DenyBashPatterns.Count == 0 || JsonArgs(arguments, "command") is not { Length: > 0 } command)
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
				return $"已被安全策略拒绝：`{toolName}` 命令匹配当前档位 `{Name}` 的危险模式 `{pattern}`。这是不可逆操作，请让用户自己决定是否执行。";
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

	private static string? JsonArgs(JsonObject arguments, string name) =>
		arguments[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
			? value.GetValue<string>()
			: null;

	private static string ResolveDirectory(string directory, string cwd) =>
		RealPath(ToolPaths.ResolveToCwd(directory, cwd));

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
}
