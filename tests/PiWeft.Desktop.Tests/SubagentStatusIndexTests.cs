using PiWeft.CodingAgent.Subagents;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 窗口级子代理索引：只跟状态边沿，不跟工具活动与 token。
public sealed class SubagentStatusIndexTests
{
	private static SubagentRun Run(
		string name,
		string sessionId,
		SubagentState state = SubagentState.Running,
		bool awaiting = false,
		string? question = null,
		string? activity = null,
		long tokens = 0,
		IReadOnlyList<string>? changedFiles = null,
		bool mayWrite = false,
		string? result = null,
		IReadOnlyList<SubagentVerifyResult>? verify = null) => new()
	{
		Name = name,
		SessionId = sessionId,
		Agent = "scout",
		State = state,
		AwaitingUser = awaiting,
		Question = question,
		Activity = activity,
		Tokens = tokens,
		ChangedFiles = changedFiles ?? [],
		MayWrite = mayWrite,
		Result = result,
		Verify = verify ?? [],
	};

	[Fact]
	public void FirstSnapshotChangesAndRepeatingItDoesNot()
	{
		var index = new SubagentStatusIndex();
		SubagentRun[] runs = [Run("scout-1", "child-1")];

		Assert.True(index.Update("parent", "父会话", runs));
		Assert.False(index.Update("parent", "父会话", [Run("scout-1", "child-1")]));
	}

	[Fact]
	public void ChangedSessionsReportRowsThatNeedTextRefresh()
	{
		var index = new SubagentStatusIndex();
		var changed = new HashSet<string>(StringComparer.Ordinal);
		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1")], changed));
		Assert.Contains("parent", changed);
		Assert.Contains("child-1", changed);

		changed.Clear();
		Assert.False(index.Update("parent", "父会话", [Run("scout-1", "child-1", activity: "read a.cs")], changed));
		Assert.Empty(changed);

		changed.Clear();
		Assert.True(index.Update("parent", "父会话", [], changed));
		Assert.Contains("child-1", changed);
	}

	[Fact]
	public void ActivityAndUsageDoNotTriggerASidebarRebuild()
	{
		var index = new SubagentStatusIndex();
		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1", activity: "read a.cs")]));

		// 每个工具起止都会改这两样；侧栏不显示它们，不该跟着重建整棵树。
		Assert.False(index.Update("parent", "父会话", [Run("scout-1", "child-1", activity: "grep foo", tokens: 9000)]));
	}

	[Fact]
	public void AwaitingAndQuestionFlipTheIndex()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话", [Run("scout-1", "child-1")]);

		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1", awaiting: true)]));
		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1", question: "走哪条路？")]));
		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1", state: SubagentState.Idle)]));
	}

	[Fact]
	public void FailedRowsCarryTheFirstResultLine()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话",
			[Run("scout-1", "child-1", state: SubagentState.Failed, result: "dotnet test 失败。\n详情")]);

		Assert.True(index.TryGet("child-1", out var entry));
		Assert.Equal("dotnet test 失败。", entry.Failure);
		// 原因没变、只是同一条失败行重新报到：侧栏不该重建。
		Assert.False(index.Update("parent", "父会话",
			[Run("scout-1", "child-1", state: SubagentState.Failed, result: "dotnet test 失败。\n详情")]));
	}

	[Fact]
	public void FailedRowsPreferHostVerificationOverTheModelReply()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话",
			[Run("scout-1", "child-1", state: SubagentState.Failed, result: "都改好了。",
				verify: [new SubagentVerifyResult { Command = "dotnet test", ExitCode = 1 }])]);

		Assert.True(index.TryGet("child-1", out var entry));
		// 验证没过时，模型那句「都改好了」不能成为侧栏/toast 的失败原因。
		Assert.Contains("验证失败", entry.Failure);
		Assert.DoesNotContain("都改好了", entry.Failure);
	}

	[Fact]
	public void RemovedRunsLeaveTheIndex()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话", [Run("scout-1", "child-1"), Run("worker-1", "child-2")]);

		Assert.True(index.Update("parent", "父会话", [Run("scout-1", "child-1")]));
		Assert.True(index.TryGet("child-1", out _));
		Assert.False(index.TryGet("child-2", out _));
		Assert.False(index.Update("parent", "父会话", [Run("scout-1", "child-1")]));
	}

	[Fact]
	public void SummariesCountActiveAndAwaitingPerParent()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent-a", "父会话",
		[
			Run("scout-1", "child-1"),
			Run("scout-2", "child-2", awaiting: true),
			Run("worker-1", "child-3", state: SubagentState.Idle),
		]);
		index.Update("parent-b", "父会话", [Run("scout-9", "child-9", state: SubagentState.Running)]);

		var summaries = index.Summaries();
		Assert.Equal((2, 1, 0), summaries["parent-a"]);
		Assert.Equal((1, 0, 0), summaries["parent-b"]);
	}

	[Fact]
	public void SummariesCountChangedFilesNotPossibleWrites()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话",
		[
			Run("worker-1", "child-1", changedFiles: ["/repo/a.cs", "/repo/b.cs"]),
			Run("worker-2", "child-2", mayWrite: true),
		]);

		// 「可能写入」不算确定改动：父行只说有一个子代理真的改了文件。
		Assert.Equal((2, 0, 1), index.Summaries()["parent"]);
	}

	[Fact]
	public void EntriesPutWaitingFirstThenRunningThenTheRest()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话",
		[
			Run("done", "child-1", state: SubagentState.Idle),
			Run("worker", "child-2"),
			Run("asking", "child-3", awaiting: true),
		]);

		var entries = index.Entries();
		Assert.Equal(["asking", "worker", "done"], entries.Select(static entry => entry.Name));
	}

	[Fact]
	public void ShrinkingOneParentDoesNotDependOnTheGlobalCount()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent-a", "A", [Run("scout-1", "child-1")]);
		index.Update("parent-b", "B", [Run("scout-2", "child-2")]);

		// P1 的过期条目被 P2 的条目把总数抬平：按全局计数判断会漏清。
		Assert.True(index.Update("parent-a", "A", [Run("scout-3", "child-3"), Run("scout-4", "child-4")]));
		Assert.False(index.TryGet("child-1", out _));
		Assert.True(index.TryGet("child-2", out _));
	}

	[Fact]
	public void TheSameChildInTwoParentsDoesNotFlipEveryFrame()
	{
		var index = new SubagentStatusIndex();
		Assert.True(index.Update("parent-a", "A", [Run("scout-1", "child-1")]));

		// fork 会复制同一条子代理记录：状态相同就不该每帧改写父会话、触发侧栏重建。
		Assert.False(index.Update("parent-b", "B", [Run("scout-1", "child-1")]));
		Assert.True(index.TryGet("child-1", out var entry));
		Assert.Equal("parent-a", entry.ParentSessionId);
	}

	[Fact]
	public void RemoveSessionDropsOneChild()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent", "父会话", [Run("scout-1", "child-1"), Run("scout-2", "child-2")]);

		Assert.True(index.RemoveSession("child-1"));
		Assert.False(index.TryGet("child-1", out _));
		Assert.True(index.TryGet("child-2", out _));
		Assert.False(index.RemoveSession(string.Empty));
		Assert.False(index.RemoveSession("missing"));
	}

	[Fact]
	public void RemoveParentDropsOnlyItsChildren()
	{
		var index = new SubagentStatusIndex();
		index.Update("parent-a", "父会话", [Run("scout-1", "child-1")]);
		index.Update("parent-b", "父会话", [Run("scout-2", "child-2")]);

		Assert.True(index.RemoveParent("parent-a"));
		Assert.False(index.TryGet("child-1", out _));
		Assert.True(index.TryGet("child-2", out _));
		Assert.False(index.RemoveParent("missing"));
	}

	[Fact]
	public void RunsWithoutASessionIdStayOut()
	{
		var index = new SubagentStatusIndex();
		// 子会话还没建好：没有可跳转的 id，等下一份快照。
		Assert.False(index.Update("parent", "父会话", [Run("scout-1", string.Empty, state: SubagentState.Starting)]));
		Assert.Empty(index.Entries());
	}
}
