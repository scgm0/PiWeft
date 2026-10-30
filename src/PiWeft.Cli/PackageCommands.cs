using PiWeft.CodingAgent;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Cli;

/// <summary>
/// <c>pi install / list / remove / update</c>：管理 pi package（npm、git 与本地路径）。
/// 与 pi 一致，这些命令在通用参数解析之前分发，因此自带开关不会与主解析器冲突。
/// </summary>
public static class PackageCommands
{
	/// <summary>顶层包命令；未命中时返回 <c>null</c>，交由通用解析器处理。</summary>
	public static async Task<int?> RunAsync(
		string command,
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		string cwd,
		CancellationToken cancellationToken)
	{
		var parsed = Parse(rest);
		if (parsed.Help)
		{
			PrintUsage(output);
			return 0;
		}

		switch (command)
		{
			case "install":
				return await InstallAsync(parsed.Source, parsed.Local, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "remove":
			case "uninstall":
				return await RemoveAsync(parsed.Source, parsed.Local, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "update":
				return await UpdateAsync(parsed, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "list":
				return List(parsed.Local, parsed.Json, cwd, output, error);
			case "config":
				return Config(parsed.Local, cwd, output, error);
			default:
				return null;
		}
	}

	private static async Task<int> InstallAsync(
		string? source,
		bool local,
		string cwd,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		if (source is null)
		{
			error.WriteLine("pi: install 需要一个包来源，例如 npm:@scope/pkg@1.0.0、git:github.com/org/repo 或 ./本地目录");
			return 2;
		}

		var options = Options(local, cwd);
		var result = await PiPackageManager.InstallAsync(source, options, runner: null, cancellationToken).ConfigureAwait(false);
		return Report(result, output, error, $"已把 {source} 写入 {options.SettingsPath}") ? 0 : 1;
	}

	private static async Task<int> RemoveAsync(
		string? source,
		bool local,
		string cwd,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		if (source is null)
		{
			error.WriteLine("pi: remove 需要一个包来源");
			return 2;
		}

		var options = Options(local, cwd);
		var result = await PiPackageManager.RemoveAsync(source, options, runner: null, cancellationToken).ConfigureAwait(false);
		return Report(result, output, error, detail: null) ? 0 : 1;
	}

	private static async Task<int> UpdateAsync(
		ParsedPackageArgs parsed,
		string cwd,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		// pi 的离线模式关闭「自动/批量」网络操作；显式 update 也属于这一类。
		if (PiEnvironment.Offline)
		{
			output.WriteLine("pi: 离线模式（--offline / PI_OFFLINE）：跳过更新。");
			return 0;
		}

		// 与 pi 对齐的别名与范围开关：pi update（自更新）、--self/self/pi、--extensions/--all、--models。
		if (parsed.Self)
		{
			output.WriteLine("pi: 本构建是 NativeAOT 单文件，请用发行页安装包或系统包管理器更新 pi 本体；包与目录更新不受影响。");
			return 0;
		}

		if (parsed.Models)
		{
			// 显式的 pi update --models 必须真的打网络：节流是给自动刷新用的，
			// 用户主动要求刷新时还回一句「没到时间」等于命令失灵。
			var outcome = await ModelCatalogStore.RefreshAllAsync(
				onError: message => error.WriteLine($"pi: model catalog {message}"),
				force: true,
				cancellationToken: cancellationToken).ConfigureAwait(false);
			var skipped = outcome.NotModified > 0 ? $"，{outcome.NotModified} 个未变化（304）" : string.Empty;
			output.WriteLine($"模型目录已刷新：{outcome.Updated} 个 Provider 更新，{outcome.Failed} 个失败{skipped}。");
			output.WriteLine($"缓存：{ModelCatalogStore.PathFor()}");
			return outcome.Failed == 0 ? 0 : 1;
		}

		// `pi update`（无来源）、--extensions 与 --all 都表示「更新全部已声明包」。
		var source = parsed.Source ?? parsed.ExtensionSource;
		var options = Options(parsed.Local, cwd);
		var results = await PiPackageManager.UpdateAsync(source, options, runner: null, cancellationToken).ConfigureAwait(false);

		if (results.Count == 0)
		{
			output.WriteLine("没有需要更新的包。");
			return 0;
		}

		var failed = 0;
		foreach (var result in results)
		{
			if (!Report(result, output, error, detail: null))
			{
				failed++;
			}
		}

		return failed == 0 ? 0 : 1;
	}

	private static int List(bool local, bool json, string cwd, TextWriter output, TextWriter error)
	{
		var agentDir = SessionPaths.GetAgentDir();
		try
		{
			var sources = PiPackageStore
				.DeclaredSources(cwd, agentDir, includeProject: true)
				.Where(candidate => !local || candidate.Scope == ResourceScope.Project)
				.ToList();

			if (json)
			{
				var items = new List<JsonNode?>(sources.Count);
				foreach (var source in sources)
				{
					var root = PiPackageStore.ResolveRoot(source);
					var installed = root is not null && Directory.Exists(root);
					var package = installed ? PiPackageStore.Load(source, root!) : null;
					items.Add(new JsonObject
					{
						["source"] = source.Value,
						["scope"] = source.Scope == ResourceScope.Project ? "project" : "user",
						["installed"] = installed,
						["root"] = root,
						["name"] = package?.Name,
						["skills"] = package?.ResolveResourcePaths(ResourcePaths.SkillsDirectoryName).Count ?? 0,
						["prompts"] = package?.ResolveResourcePaths(ResourcePaths.PromptsDirectoryName).Count ?? 0,
						["themes"] = package?.ResolveResourcePaths(ResourcePaths.ThemesDirectoryName).Count ?? 0,
					});
				}

				output.WriteLine(new JsonObject { ["packages"] = new JsonArray([.. items]) }.ToJsonString());
				return 0;
			}

			if (sources.Count == 0)
			{
				output.WriteLine("没有声明任何 pi 包。用 pi install <source> 添加。");
				return 0;
			}

			foreach (var source in sources)
			{
				var root = PiPackageStore.ResolveRoot(source);
				var installed = root is not null && Directory.Exists(root);
				var scope = source.Scope == ResourceScope.Project ? "项目" : "用户";
				var state = installed ? "已安装" : "未安装";
				output.WriteLine($"{source.Value}  [{scope}] {state}");
				if (installed)
				{
					var package = PiPackageStore.Load(source, root!);
					var skills = package.ResolveResourcePaths(ResourcePaths.SkillsDirectoryName).Count;
					var prompts = package.ResolveResourcePaths(ResourcePaths.PromptsDirectoryName).Count;
					var themes = package.ResolveResourcePaths(ResourcePaths.ThemesDirectoryName).Count;
					output.WriteLine($"    {package.Name}  {root}");
					output.WriteLine($"    技能 {skills} / 提示词 {prompts} / 主题 {themes}");
				}
			}

			return 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			error.WriteLine("pi: 无法读取包列表");
			return 1;
		}
	}

	/// <summary>打印一条结果；成功返回 true。</summary>
	private static bool Report(PackageResult result, TextWriter output, TextWriter error, string? detail)
	{
		if (result.Ok)
		{
			output.WriteLine(result.Message);
			if (detail is { Length: > 0 })
			{
				output.WriteLine($"    {detail}");
			}

			return true;
		}

		error.WriteLine($"pi: {result.Message}");
		if (result.Detail is { Length: > 0 } extra)
		{
			error.WriteLine($"    {extra.Trim()}");
		}

		return false;
	}

	private static PackageOptions Options(bool local, string cwd)
	{
		var agentDir = SessionPaths.GetAgentDir();
		var settingsPath = local ? CodingAgentSettings.ProjectPath(cwd) : CodingAgentSettings.GlobalPath(agentDir);
		return new PackageOptions
		{
			AgentDir = agentDir,
			Cwd = cwd,
			Local = local,
			NpmCommand = CodingAgentSettings.NpmCommand(settingsPath),
		};
	}

	/// <summary>包命令的解析结果，包含 pi 的 update 范围开关。</summary>
	private sealed record ParsedPackageArgs
	{
		public string? Source { get; init; }

		public bool Local { get; init; }

		public bool Json { get; init; }

		public bool Help { get; init; }

		/// <summary><c>pi update --self</c> / <c>self</c> / <c>pi</c>。</summary>
		public bool Self { get; init; }

		/// <summary><c>pi update --extensions</c>（更新全部包）。</summary>
		public bool Extensions { get; init; }

		/// <summary><c>pi update --all</c>。</summary>
		public bool All { get; init; }

		/// <summary><c>pi update --models</c>。</summary>
		public bool Models { get; init; }

		/// <summary><c>pi update --force</c>（本端口无自更新语义，接受并忽略）。</summary>
		public bool Force { get; init; }

		/// <summary><c>pi update --extension &lt;source&gt;</c> 的别名形式。</summary>
		public string? ExtensionSource { get; init; }
	}

	private static ParsedPackageArgs Parse(IReadOnlyList<string> rest)
	{
		ParsedPackageArgs parsed = new();

		for (var index = 0; index < rest.Count; index++)
		{
			var item = rest[index];
			switch (item)
			{
				case "--local" or "-l":
					parsed = parsed with { Local = true };
					break;
				case "--global" or "-g":
					parsed = parsed with { Local = false };
					break;
				case "--json":
					parsed = parsed with { Json = true };
					break;
				case "--help" or "-h":
					parsed = parsed with { Help = true };
					break;
				case "--self":
					parsed = parsed with { Self = true };
					break;
				case "--extensions":
					parsed = parsed with { Extensions = true };
					break;
				case "--all":
					parsed = parsed with { All = true };
					break;
				case "--offline":
					// 包命令在通用解析之前分发，因此这里也要认 pi 的全局离线开关。
					PiEnvironment.Configure(offlineArgument: true);
					break;
				case "--models":
					parsed = parsed with { Models = true };
					break;
				case "--force":
					parsed = parsed with { Force = true };
					break;
				case "--extension":
					if (index + 1 < rest.Count)
					{
						index++;
						parsed = parsed with { ExtensionSource = rest[index] };
					}

					break;
				default:
					// 首个非选项参数是包来源；pi 的 `update self`/`update pi` 是 --self 的别名。
					if (!item.StartsWith('-'))
					{
						if (parsed.Source is null)
						{
							parsed = parsed with
							{
								Source = item is "self" or "pi" ? null : item,
								Self = item is "self" or "pi",
							};
						}
					}

					break;
			}
		}

		return parsed;
	}

	/// <summary><c>pi config</c>：列出已声明的包来源与资源键，行式 REPL 没有交互式选择界面。</summary>
	private static int Config(bool local, string cwd, TextWriter output, TextWriter error)
	{
		var agentDir = SessionPaths.GetAgentDir();
		var settingsPath = local ? CodingAgentSettings.ProjectPath(cwd) : CodingAgentSettings.GlobalPath(agentDir);
		output.WriteLine($"写入目标: {settingsPath}");
		output.WriteLine("可编辑键: packages / skills / prompts / themes（数组，路径或包来源）。");

		try
		{
			var sources = PiPackageStore.DeclaredSources(cwd, agentDir, includeProject: true);
			if (sources.Count == 0)
			{
				output.WriteLine("尚未声明任何包。用 `pi install <source>` 添加。");
				return 0;
			}

			foreach (var source in sources)
			{
				var scope = local && source.Scope != ResourceScope.Project ? "（跳过：非项目级）" : string.Empty;
				output.WriteLine($"  [{source.Scope}] {source.Value}{scope}");
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			error.WriteLine($"pi: 无法读取包声明：{exception.Message}");
			return 1;
		}

		return 0;
	}

	private static void PrintUsage(TextWriter output)
	{
		output.WriteLine("""
			pi 包命令：
			  pi install <source> [--local|-l]   安装 npm / git / 本地 pi 包
			  pi list [--json] [--local|-l]      列出已声明的包与安装状态
			  pi remove <source> [--local|-l]    移除包（别名 uninstall）
			  pi config [--local|-l]             查看包来源与可编辑的资源键
			  pi update [source] [--local|-l]    更新一个或全部包
			  pi update --extensions             更新全部包
			  pi update --self | self | pi       pi 本体更新（本构建交由系统包管理器）
			  pi update --models                 模型目录（内嵌，随 pi 更新）

			来源形式：
			  npm:@scope/pkg@1.0.0   装到 <agentDir>/npm/node_modules
			  git:github.com/org/repo@v1   克隆到 <agentDir>/git
			  ./local-pkg            只登记路径，不复制

			默认写全局 <agentDir>/settings.json；--local 改项目 .pi/settings.json。
			""");
	}
}
