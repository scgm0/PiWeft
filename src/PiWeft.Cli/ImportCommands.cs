using PiWeft.CodingAgent.Import;

namespace PiWeft.Cli;

/// <summary>
/// <c>pi import</c>：把别的工具留下的会话搬进 pi 的会话目录。
/// <para>
/// 在通用参数解析之前分发（与 <c>pi mcp</c> / <c>pi install</c> 同层），因为它自带
/// <c>--from</c> / <c>--project</c> / <c>--cwd</c> 这类开关，交给主解析器会被当成未知参数。
/// </para>
/// <para>
/// 无参数时只列可导入会话，不改任何文件；要真落盘必须显式给 <c>--all</c> 或一个序号/路径。
/// 「列出来就会写盘」在导入这种不可逆动作上是错的默认。
/// </para>
/// </summary>
public static class ImportCommands
{
	/// <param name="home">来源根目录的「家目录」；测试注入用，生产传 <see langword="null"/> 取当前用户。</param>
	/// <param name="sessionDir">产物落盘目录；为空时落在 <paramref name="cwd"/> 对应的默认会话目录。</param>
	public static int Run(
		IReadOnlyList<string> rest,
		TextWriter output,
		TextWriter error,
		string cwd,
		string? home = null,
		string? sessionDir = null)
	{
		var options = Parse(rest);
		if (options.Help)
		{
			PrintUsage(output);
			return 0;
		}

		if (options.Sources)
		{
			return PrintSources(output, cwd, home);
		}

		if (options.Errors.Count > 0)
		{
			foreach (var message in options.Errors)
			{
				error.WriteLine($"pi import: {message}");
			}

			return 2;
		}

		// 先校验来源名再列举：Discover 会直接抛参数异常，用户应当看到「可用的是哪些」。
		if (options.From is { Length: > 0 } unknown && SessionImportService.Find(unknown) is null)
		{
			error.WriteLine(
				$"pi import: 未知来源「{unknown}」。可用：{string.Join('、', SessionImportService.Importers.Select(static item => item.Key))}");
			return 2;
		}

		var sessions = options.From is { Length: > 0 }
			? SessionImportService.Discover(options.From, options.Project, home)
			: SessionImportService.DiscoverAll(options.Project, home);

		if (sessions.Count == 0)
		{
			error.WriteLine(options.Project is { Length: > 0 }
				? $"pi import: {options.Project} 下没有可导入的外部会话。"
				: "pi import: 没有找到可导入的外部会话。");
			return 1;
		}

		// 列清单模式：不写盘。
		if (!options.All && options.Selection is null)
		{
			PrintSessions(sessions, output, options.Project);
			output.WriteLine();
			output.WriteLine("用 `pi import <序号>` 或 `pi import --all` 导入。");
			return 0;
		}

		var targets = new List<ImportedSession>();
		if (options.All)
		{
			targets.AddRange(sessions);
		}
		else if (options.Selection is { } selection)
		{
			if (int.TryParse(selection, out var index))
			{
				if (index < 1 || index > sessions.Count)
				{
					error.WriteLine($"pi import: 序号 {index} 超出范围（1–{sessions.Count}）。");
					return 2;
				}

				targets.Add(sessions[index - 1]);
			}
			else
			{
				// 也接受直接给源文件路径：脚本化场景下不留序号歧义。
				var match = sessions.FirstOrDefault(session =>
					string.Equals(Path.GetFullPath(session.SourcePath), Path.GetFullPath(selection), StringComparison.Ordinal));
				if (match is null)
				{
					error.WriteLine($"pi import: 找不到会话「{selection}」。先运行 `pi import` 看清单。");
					return 2;
				}

				targets.Add(match);
			}
		}

		var failed = 0;
		foreach (var session in targets)
		{
			var result = SessionImportService.Import(session, sessionDir, cwdOverride: options.Cwd);
			if (!result.Ok)
			{
				failed++;
				error.WriteLine($"pi import: {session.Source} · {session.SessionId} 导入失败：{result.Error}");
				continue;
			}

			output.WriteLine($"已导入 {session.Source} · {session.SessionId} → {result.SessionFile}（{result.MessageCount} 条消息）");
		}

		if (targets.Count > 1)
		{
			output.WriteLine($"共 {targets.Count - failed} 条成功，{failed} 条失败。");
		}

		return failed == 0 ? 0 : 1;
	}

	private static int PrintSources(TextWriter output, string cwd, string? home)
	{
		output.WriteLine("可导入的外部会话来源：");
		foreach (var source in SessionImportService.DiscoverSources(home))
		{
			var state = source.Exists ? $"{source.Count} 条" : "未发现";
			output.WriteLine($"  {source.Key,-8} {source.Label,-14} {state,-8} {source.Root}");
		}

		output.WriteLine();
		output.WriteLine($"当前目录：{cwd}");
		return 0;
	}

	private static void PrintSessions(IReadOnlyList<ImportedSession> sessions, TextWriter output, string? project)
	{
		output.WriteLine(project is { Length: > 0 }
			? $"{project} 下可导入的外部会话（{sessions.Count} 条）："
			: $"可导入的外部会话（{sessions.Count} 条，全部项目）：");

		for (var index = 0; index < sessions.Count; index++)
		{
			var session = sessions[index];
			var title = session.Title.Length > 0 ? session.Title : "（无标题）";
			output.WriteLine(
				$"  {index + 1,3}. [{session.Source}] {session.Modified.LocalDateTime:yyyy-MM-dd HH:mm}  {title}");
			output.WriteLine($"       {session.Cwd}");
		}
	}

	private sealed record Options(
		string? From,
		string? Project,
		string? Cwd,
		string? Selection,
		bool All,
		bool Sources,
		bool Help,
		IReadOnlyList<string> Errors);

	private static Options Parse(IReadOnlyList<string> args)
	{
		string? from = null;
		string? project = null;
		string? workDir = null;
		string? selection = null;
		var all = false;
		var sources = false;
		var help = false;
		var errors = new List<string>();

		for (var index = 0; index < args.Count; index++)
		{
			switch (args[index])
			{
				case "-h" or "--help":
					help = true;
					break;
				case "--sources":
					sources = true;
					break;
				case "--all":
					all = true;
					break;
				case "--from" when index + 1 < args.Count:
					from = args[++index];
					break;
				case "--project" when index + 1 < args.Count:
					project = args[++index];
					break;
				case "--cwd" when index + 1 < args.Count:
					workDir = args[++index];
					break;
				default:
					if (args[index].StartsWith('-'))
					{
						errors.Add($"未知选项 {args[index]}");
					}
					else if (selection is null)
					{
						selection = args[index];
					}
					else
					{
						errors.Add($"多余的参数 {args[index]}");
					}

					break;
			}
		}

		return new Options(
			from,
			project is null ? null : Path.GetFullPath(project),
			workDir is null ? null : Path.GetFullPath(workDir),
			selection,
			all,
			sources,
			help,
			errors);
	}

	private static void PrintUsage(TextWriter output) => output.WriteLine(
		"""
		用法：pi import [选项] [序号|源文件路径]

		列出并导入其它工具留下的会话（Claude Code、Codex CLI）。不带选项时只列清单，不写盘。

		选项：
		  --from <key>        只看一个来源（claude / codex）
		  --project <dir>     只列工作目录在该目录下的会话（默认全部）
		  --cwd <dir>         导入后会话记录的工作目录（源目录已不存在时用）
		  --all               导入列出的全部会话
		  --sources           只看各来源的根目录与条数
		  -h, --help          显示本帮助

		示例：
		  pi import                         列出所有可导入会话
		  pi import --project .             只看当前项目的
		  pi import 3                       导入清单里的第 3 条
		  pi import --from codex --all      导入全部 Codex 会话
		""");
}
