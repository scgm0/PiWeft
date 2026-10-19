using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Tools;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

public class SystemPromptTests
{
	private static SystemPromptOptions Options(string cwd) => new()
	{
		Cwd = cwd,
		Tools = BuiltinTools.Create(cwd),
		ExtraGuidelines = SystemPrompt.BaselineGuidelines,
	};

	[Fact]
	public void Build_EmitsPiSectionNamesInOrder()
	{
		var message = SystemPrompt.Build(Options("/workspace/project"));

		Assert.NotNull(message.Sections);
		Assert.Equal(
			["preamble", "tools", "rules", "cwd"],
			message.Sections!.Keys.ToList());

		Assert.Equal(SystemPrompt.Preamble, message.Sections["preamble"]);
		Assert.Equal("<cwd>\n/workspace/project\n</cwd>", message.Sections["cwd"]);
		Assert.Equal(BuiltinTools.Create("/workspace/project").Count, message.ToolsAdded!.Count);
	}

	[Fact]
	public void Build_ToolsSectionMatchesPiFormat()
	{
		var message = SystemPrompt.Build(Options("/tmp"));
		var tools = message.Sections!["tools"]!;

		Assert.StartsWith("<tools>\n", tools);
		Assert.EndsWith(
			"\nIn addition to the tools above, you may have access to other custom tools depending on the project.\n</tools>",
			tools);
		Assert.Contains("- read: Read file contents\n", tools);
		Assert.Contains("- bash: Execute bash commands (ls, grep, find, etc.)\n", tools);
		Assert.Contains("- ls: List directory contents\n", tools);
	}

	[Fact]
	public void Build_RulesMatchPiText()
	{
		var message = SystemPrompt.Build(Options("/tmp"));

		Assert.Equal(
			"""
			<rules>
			- Use read to examine files instead of cat or sed.
			- You can inspect PI_* environment variables for current model and session details.
			- Use edit for precise changes (edits[].oldText must match exactly)
			- When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls
			- Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.
			- Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions.
			- Use write only for new files or complete rewrites.
			- Maintain the actionable plan with the todo tool. After finishing an item, call action=update with its id and status=completed. Remove one obsolete item with action=delete and its id. Use action=replace only to rebuild the whole plan. If an id is uncertain, call action=list first.
			- Start a new or materially re-scoped task with one action=replace call containing the complete new plan. Never infer that boundary from completed items, idle time, a user message, or session start.
			- If a replacement was mistaken, call action=restore immediately. Use action=clear only when intentionally discarding the active plan.
			- Todo state is per-branch: switching branches restores that branch's plan.
			- IMPORTANT RULE: Whenever you need ANY input from the user (a choice, confirmation, text, or multi-line content), you MUST use the ask_question tool. Do NOT write questions in plain text — that forces the user to type free-form replies and breaks the desktop UI interaction flow.
			- Pass all questions in ONE questions array per call, never split related questions into repeated calls.
			- Types: select = pick from options (strings or {label, value?, description?}; a custom free-text input is always shown); multi_select = pick multiple, returns an array; confirm = yes/no (e.g. destructive or irreversible actions); input = short text; editor = multi-line content like code.
			- Be concise in your responses
			- Show file paths clearly when working with files
			</rules>
			""",
			message.Sections!["rules"]);
	}

	[Fact]
	public void Build_WithoutGuidelines_OmitsRules()
	{
		var message = SystemPrompt.Build(new SystemPromptOptions { Cwd = "/tmp" });
		Assert.False(message.Sections!.ContainsKey("rules"));
	}

	[Fact]
	public void Build_AppliesSectionOverrides()
	{
		var message = SystemPrompt.Build(Options("/tmp") with
		{
			SectionOverrides = new Dictionary<string, string?>
			{
				["skills"] = "<skills>\n- demo\n</skills>",
				["rules"] = null,
			},
		});

		Assert.False(message.Sections!.ContainsKey("rules"));
		Assert.Equal("<skills>\n- demo\n</skills>", message.Sections["skills"]);
	}

	[Fact]
	public void ContextFiles_LoadsAgentsMdFromTheWorkingDirectory()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-context-" + Guid.NewGuid().ToString("N")[..8]);
		var nested = Path.Combine(root, "src", "deep");
		Directory.CreateDirectory(nested);
		File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Rules\nbe nice");
		File.WriteAllText(Path.Combine(nested, "AGENTS.md"), "# Local\nbe local");

		try
		{
			var context = ContextFiles.Load(nested);

			Assert.NotNull(context);
			Assert.Contains("<project_context>", context);
			Assert.Contains("# Rules", context);
			Assert.Contains("# Local", context);
			// Outer file first, then the nearer one.
			Assert.True(context!.IndexOf("# Rules", StringComparison.Ordinal) < context.IndexOf("# Local", StringComparison.Ordinal));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ContextFiles_ReturnsNullWithoutInstructionFiles()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-noctx-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			// A temp directory usually has no AGENTS.md, but do not assume: only assert the shape.
			var context = ContextFiles.Load(root);
			if (context is not null)
			{
				Assert.Contains("<project_context>", context);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}

/// <summary>
/// Compares the generated prompt with the system messages stored in real pi session files, so prompt drift is caught
/// the same way session format drift is.
/// </summary>
public class RealPromptFidelityTests
{
	private static SystemMessage? FirstSectionedSystemMessage()
	{
		foreach (var system in SectionedSystemMessages())
		{
			return system;
		}

		return null;
	}

	/// <summary>枚举真实会话文件里的分章节系统提示词。</summary>
	private static IEnumerable<SystemMessage> SectionedSystemMessages()
	{
		var root = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
		if (!Directory.Exists(root))
		{
			yield break;
		}

		foreach (var path in Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories))
		{
			foreach (var line in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}

				if (SessionJson.ParseLine(line) is SessionMessageEntry { Message: SystemMessage { Sections: { Count: > 0 } } system })
				{
					yield return system;
				}
			}
		}
	}

	[Fact]
	public void PreambleSectionMatchesPi()
	{
		var real = FirstSectionedSystemMessage();
		if (real?.Sections is null)
		{
			return; // no pi sessions on this machine
		}

		Assert.Equal(SystemPrompt.Preamble, real.Sections["preamble"]);
	}

	[Fact]
	public void CwdSectionMatchesPi()
	{
		var real = FirstSectionedSystemMessage();
		if (real?.Sections is null)
		{
			return;
		}

		var cwd = "/tmp/fidelity-check";
		var generated = SystemPrompt.Build(new SystemPromptOptions { Cwd = cwd });
		Assert.Equal($"<cwd>\n{cwd}\n</cwd>", generated.Sections!["cwd"]);
		Assert.StartsWith("<cwd>", real.Sections["cwd"]);
	}

	[Fact]
	public void ToolAndRuleLinesMatchPi()
	{
		// 用所有真实会话的并集，而不是第一份：todo 这类工具由扩展提供，
		// 早期会话可能没有，但只要有任一会话声明过，就应该能按 pi 原文匹配上。
		var sessions = SectionedSystemMessages().ToList();
		if (sessions.Count == 0)
		{
			return;
		}

		var generated = SystemPrompt.Build(new SystemPromptOptions
		{
			Cwd = "/tmp",
			Tools = BuiltinTools.Create("/tmp"),
			ExtraGuidelines = SystemPrompt.BaselineGuidelines,
		});

		var realToolLines = Sections(sessions, "tools");
		var ourToolLines = Lines(generated.Sections!["tools"]!, "- ");

		// Every tool line we produce must match pi's text exactly, and pi may declare more tools than we implement.
		Assert.All(ourToolLines, line => Assert.Contains(line, realToolLines));

		var realRuleLines = Sections(sessions, "rules");
		var ourRuleLines = Lines(generated.Sections!["rules"]!, "- ");
		Assert.All(ourRuleLines, line => Assert.Contains(line, realRuleLines));
	}

	private static HashSet<string> Sections(IEnumerable<SystemMessage> sessions, string name)
	{
		var lines = new HashSet<string>(StringComparer.Ordinal);
		foreach (var session in sessions)
		{
			if (session.Sections!.TryGetValue(name, out var section) && section is { Length: > 0 })
			{
				lines.UnionWith(Lines(section, "- "));
			}
		}

		return lines;
	}

	private static List<string> Lines(string section, string prefix) =>
		[.. section.Split('\n').Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).Select(static line => line.Trim())];
}

/// <summary>
/// 项目指令文件的加载规则：全局文件优先、每级目录只取一个、按真实路径去重，
/// 以及嵌套 git worktree 里符号链接到主仓库的那份要被跳过。
/// </summary>
public class ContextFileLoadingTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-ctx-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ContextFileLoadingTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private static void Write(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	[Fact]
	public void GlobalFileComesFirstAndInnerDirectoriesComeLast()
	{
		Write(Path.Combine(_agentDir, "AGENTS.md"), "全局规则");
		var outer = Path.Combine(_root, "repo");
		var inner = Path.Combine(outer, "src");
		Write(Path.Combine(outer, "AGENTS.md"), "仓库规则");
		Write(Path.Combine(inner, "AGENTS.md"), "子目录规则");

		var context = ContextFiles.Load(inner, _agentDir);

		Assert.NotNull(context);
		var order = new[] { "全局规则", "仓库规则", "子目录规则" }
			.Select(text => context!.IndexOf(text, StringComparison.Ordinal))
			.ToList();
		Assert.All(order, index => Assert.True(index >= 0));
		Assert.True(order[0] < order[1], "全局文件应排在最前");
		Assert.True(order[1] < order[2], "外层目录应排在内层之前");
	}

	[Fact]
	public void OnlyOneFilePerDirectoryIsLoaded()
	{
		var project = Path.Combine(_root, "repo");
		Write(Path.Combine(project, "AGENTS.md"), "来自 AGENTS");
		Write(Path.Combine(project, "CLAUDE.md"), "来自 CLAUDE");

		var context = ContextFiles.Load(project, _agentDir);

		Assert.NotNull(context);
		Assert.Contains("来自 AGENTS", context);
		Assert.DoesNotContain("来自 CLAUDE", context);
	}

	[Fact]
	public void SymlinkedFilesAreLoadedOnlyOnce()
	{
		var project = Path.Combine(_root, "repo");
		Write(Path.Combine(project, "AGENTS.md"), "同一份说明");
		// 常见的写法：CLAUDE.md 指向 AGENTS.md。
		File.CreateSymbolicLink(Path.Combine(project, "CLAUDE.md"), Path.Combine(project, "AGENTS.md"));

		var context = ContextFiles.Load(project, _agentDir);

		Assert.NotNull(context);
		Assert.Equal(1, CountOccurrences(context!, "同一份说明"));
	}

	[Fact]
	public void SymlinkedDirectoryIsNotLoadedTwice()
	{
		var outer = Path.Combine(_root, "outer");
		Write(Path.Combine(outer, "AGENTS.md"), "外层说明");
		var link = Path.Combine(_root, "linked");
		Directory.CreateSymbolicLink(link, outer);

		var context = ContextFiles.Load(link, _agentDir);

		Assert.NotNull(context);
		Assert.Equal(1, CountOccurrences(context!, "外层说明"));
	}

	[Fact]
	public void WorktreeFileSymlinkedToTheMainRepoIsSkipped()
	{
		// 主仓库 + 嵌在它内部的 worktree（git worktree add 到仓库子目录）。
		var main = Path.Combine(_root, "main");
		var worktree = Path.Combine(main, "wt");
		Directory.CreateDirectory(worktree);

		var gitDir = Path.Combine(main, ".git");
		var worktreeGitDir = Path.Combine(gitDir, "worktrees", "wt");
		Directory.CreateDirectory(worktreeGitDir);
		File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");
		File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}");

		Write(Path.Combine(main, "AGENTS.md"), "主仓库规则");
		// worktree 的 AGENTS.md 只是指向主仓库那份的符号链接。
		File.CreateSymbolicLink(Path.Combine(worktree, "AGENTS.md"), Path.Combine(main, "AGENTS.md"));

		var context = ContextFiles.Load(worktree, _agentDir);

		Assert.NotNull(context);
		Assert.Equal(1, CountOccurrences(context!, "主仓库规则"));
		Assert.Contains(Path.Combine(worktree, "AGENTS.md"), context!);
	}

	[Fact]
	public void OrdinaryRepoWithoutSymlinksStillLoadsEverything()
	{
		var main = Path.Combine(_root, "plain");
		Directory.CreateDirectory(Path.Combine(main, ".git"));

		Write(Path.Combine(main, "AGENTS.md"), "普通仓库规则");

		var context = ContextFiles.Load(main, _agentDir);

		Assert.NotNull(context);
		Assert.Contains("普通仓库规则", context);
	}

	private static int CountOccurrences(string haystack, string needle)
	{
		var count = 0;
		var index = 0;
		while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += needle.Length;
		}

		return count;
	}
}

/// <summary>
/// 用户给出的路径里的 <c>~</c> 必须展开：<c>Path.GetFullPath("~/x")</c> 会得到
/// <c>&lt;cwd&gt;/~/x</c>，路径永远不存在（pi 1.2.0 修的同类问题）。
/// </summary>
public class UserPathTests
{
	[Fact]
	public void TildeAloneIsTheHomeDirectory() =>
		Assert.Equal("/home/tester", UserPath.ExpandTilde("~", "/home/tester"));

	[Fact]
	public void TildePrefixIsReplaced()
	{
		var expanded = UserPath.ExpandTilde("~/skills/pdf", "/home/tester");

		Assert.Equal(Path.Combine("/home/tester", "skills/pdf"), expanded);
	}

	[Fact]
	public void WindowsStyleTildeIsAlsoExpanded()
	{
		// 反斜杠写法同样要展开（Windows 上 ~\\x 与 ~/x 等价）。
		var expanded = UserPath.ExpandTilde(@"~\skills\pdf", "/home/tester");

		Assert.StartsWith("/home/tester", expanded, StringComparison.Ordinal);
		Assert.EndsWith("skills\\pdf", expanded, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("/absolute/path")]
	[InlineData("relative/path")]
	[InlineData("~other/path")]
	public void OtherPathsAreUntouched(string input) =>
		Assert.Equal(input, UserPath.ExpandTilde(input, "/home/tester"));

	[Fact]
	public void ResolveReturnsAnAbsoluteExpandedPath()
	{
		var resolved = UserPath.Resolve("~/pi-agent", "/home/tester");

		Assert.True(Path.IsPathRooted(resolved));
		Assert.Equal(Path.Combine("/home/tester", "pi-agent"), resolved);
	}
}
