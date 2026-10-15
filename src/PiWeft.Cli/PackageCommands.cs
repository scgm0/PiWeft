using System.Text.Json.Nodes;
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
		var (source, local, json, help) = Parse(rest);
		if (help)
		{
			PrintUsage(output);
			return 0;
		}

		switch (command)
		{
			case "install":
				return await InstallAsync(source, local, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "remove":
			case "uninstall":
				return await RemoveAsync(source, local, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "update":
				return await UpdateAsync(source, local, cwd, output, error, cancellationToken).ConfigureAwait(false);
			case "list":
				return List(local, json, cwd, output, error);
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
		string? source,
		bool local,
		string cwd,
		TextWriter output,
		TextWriter error,
		CancellationToken cancellationToken)
	{
		var options = Options(local, cwd);
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

	private static (string? Source, bool Local, bool Json, bool Help) Parse(IReadOnlyList<string> rest)
	{
		string? source = null;
		var local = false;
		var json = false;
		var help = false;

		foreach (var item in rest)
		{
			switch (item)
			{
				case "--local" or "-l":
					local = true;
					break;
				case "--global" or "-g":
					local = false;
					break;
				case "--json":
					json = true;
					break;
				case "--help" or "-h":
					help = true;
					break;
				default:
					// 首个非选项参数是包来源；其余忽略，避免误把值当成来源。
					if (!item.StartsWith('-') && source is null)
					{
						source = item;
					}

					break;
			}
		}

		return (source, local, json, help);
	}

	private static void PrintUsage(TextWriter output)
	{
		output.WriteLine("""
			pi 包命令：
			  pi install <source> [--local|-l]   安装 npm / git / 本地 pi 包
			  pi list [--json] [--local|-l]      列出已声明的包与安装状态
			  pi remove <source> [--local|-l]    移除包（别名 uninstall）
			  pi update [source] [--local|-l]    更新一个或全部包

			来源形式：
			  npm:@scope/pkg@1.0.0   装到 <agentDir>/npm/node_modules
			  git:github.com/org/repo@v1   克隆到 <agentDir>/git
			  ./local-pkg            只登记路径，不复制

			默认写全局 <agentDir>/settings.json；--local 改项目 .pi/settings.json。
			""");
	}
}
