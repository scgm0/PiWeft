using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Subagents;
using PiWeft.CodingAgent.Tools;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 交接简报的排版：一页 Markdown，写清在哪、改了什么、还差什么。
/// 全是纯函数，不读 git、不碰窗口。
public sealed class HandoffBriefTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 26, 19, 5, 0, TimeSpan.FromHours(8));

	[Fact]
	public void BriefCarriesGoalPlanTodosAndWorkspace()
	{
		var brief = HandoffBrief.Build(Input());

		Assert.Contains("# 交接简报：整理 Git 面板", brief);
		Assert.Contains("- 目录：`/repo`", brief);
		Assert.Contains("## 目标", brief);
		Assert.Contains("> 把 Git 面板补齐", brief);
		Assert.Contains("- 阶段：进行中 · 轮次 3/32", brief);
		Assert.Contains("- [x] 1. 放弃更改", brief);
		Assert.Contains("- [ ] 2. 冲突收尾", brief);
		Assert.Contains("`进行中` #1 补测试", brief);
		Assert.Contains("- 分支：main（↑2 ↓1）", brief);
		Assert.Contains("- 未提交：2 个文件", brief);
		Assert.Contains("`src/App.cs`（修改）", brief);
		Assert.Contains("`src/New.cs`（未跟踪）", brief);
		Assert.Contains("最近一轮工具改动", brief);
		Assert.Contains("`src/App.cs` ×2 +5 −1", brief);
		Assert.Contains("worker-1 · 运行中 · 修掉分组滚动 · 改过 3 个文件", brief);
		Assert.Contains("- 用户：把 Git 面板补齐", brief);
		Assert.Contains("- 助手：全部测试通过。", brief);
		Assert.Contains("## 接手建议", brief);
		// 建议从真实状态生成，不是两句通用话。
		Assert.Contains("工作区有 2 个未提交文件", brief);
		Assert.Contains("分支与上游差 ↑2 ↓1", brief);
		Assert.Contains("还有 1 个子代理在跑", brief);
		Assert.Contains("按计划继续第 2 步：冲突收尾", brief);
	}

	[Fact]
	public void NextStepsSurfaceConflictsAndBlockedGoals()
	{
		var brief = HandoffBrief.Build(Input() with
		{
			Goal = new GoalState(true, GoalState.Blocked, "目标", 3, 32, "等待凭据"),
			Uncommitted = [new GitChange("U", "U", "conflict.md")],
			Subagents = [],
			PlanSteps = [],
			Todos = [],
		});

		Assert.Contains("先处理目标阻塞：等待凭据", brief);
		Assert.Contains("先解决合并冲突", brief);
		Assert.DoesNotContain("按计划继续", brief);
	}

	[Fact]
	public void EmptySessionSaysNoneInsteadOfInventingContent()
	{
		var brief = HandoffBrief.Build(Input() with
		{
			Goal = GoalState.Initial,
			PlanSteps = [],
			Todos = [],
			Uncommitted = [],
			TurnChanges = [],
			Subagents = [],
			RecentUserMessages = [],
			LastAssistant = string.Empty,
			GitError = "不是 Git 仓库",
		});

		Assert.Contains("## 目标\n无。", brief);
		Assert.Contains("## 计划\n无。", brief);
		Assert.Contains("## 待办\n无。", brief);
		Assert.Contains("- Git：不是 Git 仓库", brief);
		Assert.DoesNotContain("## 子代理", brief);
		Assert.DoesNotContain("## 最近的对话", brief);
	}

	[Fact]
	public void LongMessagesAreTrimmedToOneLine()
	{
		var longUser = new string('问', HandoffBrief.UserMessageLimit + 50);
		var longAssistant = new string('答', HandoffBrief.AssistantLimit + 80);
		var brief = HandoffBrief.Build(Input() with
		{
			RecentUserMessages = [longUser],
			LastAssistant = longAssistant,
		});

		Assert.Contains("问…", brief);
		Assert.Contains("…答", brief);
		Assert.DoesNotContain(longUser, brief);
	}

	[Fact]
	public void TooManyUncommittedFilesAreCountedNotListed()
	{
		var changes = Enumerable.Range(0, HandoffBrief.MaxUncommitted + 5)
			.Select(index => new GitChange(" ", "M", $"src/File{index}.cs"))
			.ToArray();

		var brief = HandoffBrief.Build(Input() with { Uncommitted = changes });

		Assert.Contains("其余 5 个未列出", brief);
	}

	private static HandoffBriefInput Input() => new(
		Title: "整理 Git 面板",
		Cwd: "/repo",
		Model: "vendor/model",
		Thinking: "high",
		SessionFile: "/agent/sessions/--repo--/s.jsonl",
		Goal: new GoalState(true, GoalState.Active, "把 Git 面板补齐", 3, 32, null),
		PlanSteps: [new(1, "放弃更改", true), new(2, "冲突收尾", false)],
		PlanExecutionEnabled: true,
		Todos: [new(1, "补测试", "in_progress"), new(2, "写文档", "pending")],
		Branch: "main",
		Ahead: 2,
		Behind: 1,
		GitError: null,
		Uncommitted: [new GitChange(" ", "M", "src/App.cs"), new GitChange("?", "?", "src/New.cs")],
		TurnChanges: [new ChatFileChange("src/App.cs", 2, 5, 1, "patch")],
		Subagents:
		[
			new SubagentRun
			{
				Name = "worker-1",
				Agent = "worker",
				Label = "修掉分组滚动",
				State = SubagentState.Running,
				ChangedFiles = ["a.cs", "b.cs", "c.cs"],
			},
		],
		RecentUserMessages: ["把 Git 面板补齐"],
		LastAssistant: "全部测试通过。",
		Now: Now);
}
