using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 窗口级子代理索引：把每个标签里的 hub 快照汇到一处，供侧栏与命令面板读。
/// 只收会改变显示的字段。当前工具（<c>Activity</c>）与 token 每次工具起止都变，
/// 跟进它们会让整棵侧栏反复重建；那些仍在各标签的子代理卡上原地刷新。
internal sealed class SubagentStatusIndex
{
	/// 一条子代理状态。名字与状态一栏够侧栏与命令面板用，任务正文不复制。
	internal sealed record Entry(
		string Name,
		string SessionId,
		string ParentSessionId,
		string ParentTitle,
		string Agent,
		SubagentState State,
		bool Awaiting,
		bool Question,
		int ChangedFiles,
		bool MayWrite,
		bool Suppressed,
		bool SteerPending,
		string? Failure)
	{
		public bool IsActive => State is SubagentState.Starting or SubagentState.Running;
	}

	private readonly Dictionary<string, Entry> _bySession = new(StringComparer.Ordinal);
	// 复用一份 id 集合：工具起止每次都会进 Update，反复 new HashSet 只是白分配。
	// 只在 UI 线程调用（ApplySubagentStatuses），不跨线程共享。
	private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
	// 侧栏每帧会读汇总；复用字典，避免每次边沿再分配一份。
	private readonly Dictionary<string, (int Active, int Awaiting, int Changed)> _summaries = new(StringComparer.Ordinal);

	/// 用父会话的最新快照替换它名下的条目；返回显示相关字段是否真的变了。
	/// 工具活动不参与比较，所以一次运行里只有开始、结束、提问、回答这几个边沿返回 true。
	internal bool Update(string parentSessionId, string parentTitle, IReadOnlyList<SubagentRun> runs, ISet<string>? changedSessions = null)
	{
		var changed = false;
		_seen.Clear();
		foreach (var run in runs)
		{
			// 子会话还没建好：没有 id 就没有可跳转的目标，等下一份快照。
			if (run.SessionId.Length == 0)
			{
				continue;
			}

			_seen.Add(run.SessionId);
			_bySession.TryGetValue(run.SessionId, out var previous);
			var failure = FailureText(run);
			// 先比字段再构造：稳定期（只有工具活动变化）不分配新 Entry。
			if (previous is not null && Matches(previous, run, failure))
			{
				continue;
			}

			changedSessions?.Add(run.SessionId);
			_bySession[run.SessionId] = new Entry(
				run.Name,
				run.SessionId,
				// fork 会把同一条子代理记录复制到两个父会话。保留首次写入的父，
				// 否则两边每帧互相改写，侧栏每个工具事件都要重建一次。
				previous?.ParentSessionId ?? parentSessionId,
				previous?.ParentTitle ?? parentTitle,
				run.Agent,
				run.State,
				run.AwaitingUser,
				run.Question is { Length: > 0 },
				run.ChangedFiles.Count,
				run.MayWrite,
				run.DeliverySuppressed,
				run.SteerPending,
				failure);
			changed = true;
		}

		// 快照里没有的条目属于这个父会话就删掉。不看全局计数：
		// 兄弟父会话的条目会让 Count 相等，过期条目就留在索引里。
		List<string>? gone = null;
		foreach (var (id, entry) in _bySession)
		{
			if (!_seen.Contains(id) &&
				string.Equals(entry.ParentSessionId, parentSessionId, StringComparison.Ordinal))
			{
				(gone ??= []).Add(id);
			}
		}

		if (gone is not null)
		{
			foreach (var id in gone)
			{
				_bySession.Remove(id);
				changedSessions?.Add(id);
			}

			changed = true;
		}

		if (changed)
		{
			changedSessions?.Add(parentSessionId);
		}

		return changed;
	}

	/// 删除一个会话文件时清掉它的条目：子代理行不再挂着一个打不开的状态。
	internal bool RemoveSession(string sessionId) =>
		sessionId.Length > 0 && _bySession.Remove(sessionId);

	/// 逐字段比较，避免为「没变」也构造一条新 Entry。
	/// 父会话不参与：同一条子代理可能在 fork 后的两个标签里各有一份快照。
	private static bool Matches(Entry entry, SubagentRun run, string? failure) =>
		string.Equals(entry.Name, run.Name, StringComparison.Ordinal) &&
		string.Equals(entry.Agent, run.Agent, StringComparison.Ordinal) &&
		entry.State == run.State &&
		entry.Awaiting == run.AwaitingUser &&
		entry.Question == (run.Question is { Length: > 0 }) &&
		entry.ChangedFiles == run.ChangedFiles.Count &&
		entry.MayWrite == run.MayWrite &&
		entry.Suppressed == run.DeliverySuppressed &&
		entry.SteerPending == run.SteerPending &&
		string.Equals(entry.Failure, failure, StringComparison.Ordinal);

	/// 侧栏、命令面板与 toast 共用的失败原因：先宿主验证结论，再模型回复首行。
	/// 只失败时返回；成功或运行中返回 null。
	internal static string? FailureText(SubagentRun run)
	{
		if (run.State != SubagentState.Failed)
		{
			return null;
		}

		foreach (var verify in run.Verify)
		{
			if (verify.Passed)
			{
				continue;
			}

			var line = verify.TimedOut
				? Strings.SubagentVerifyTimedOut(verify.Command)
				: Strings.SubagentVerifyFailed(verify.Command, verify.ExitCode ?? -1);
			return line.Length <= 120 ? line : string.Concat(line.AsSpan(0, 120), "…");
		}

		if (run.Result is not { Length: > 0 } text)
		{
			return string.Empty;
		}

		var span = text.AsSpan().Trim();
		var newline = span.IndexOfAny('\n', '\r');
		if (newline > 0)
		{
			span = span[..newline];
		}

		span = span.TrimEnd();
		return span.Length <= 120 ? span.ToString() : string.Concat(span[..120], "…");
	}

	/// 父会话的标签关了：它名下的条目一起收走，免得迟到的刷新把行又挂回来。
	internal bool RemoveParent(string parentSessionId)
	{
		List<string>? gone = null;
		foreach (var (id, entry) in _bySession)
		{
			if (string.Equals(entry.ParentSessionId, parentSessionId, StringComparison.Ordinal))
			{
				(gone ??= []).Add(id);
			}
		}

		if (gone is null)
		{
			return false;
		}

		foreach (var id in gone)
		{
			_bySession.Remove(id);
		}

		return true;
	}

	internal bool TryGet(string sessionId, out Entry entry) => _bySession.TryGetValue(sessionId, out entry!);

	/// 按父会话汇总。侧栏每行查一次，所以先按父会话分好。
	/// 返回具体字典（别名共享）：调用方只读、同一线程上立即使用。
	internal Dictionary<string, (int Active, int Awaiting, int Changed)> Summaries()
	{
		if (_bySession.Count == 0)
		{
			return EmptySummaries;
		}

		_summaries.Clear();
		foreach (var entry in _bySession.Values)
		{
			_summaries.TryGetValue(entry.ParentSessionId, out var current);
			var active = current.Active + (entry.IsActive ? 1 : 0);
			var awaiting = current.Awaiting + (entry.Awaiting || entry.Question ? 1 : 0);
			var changed = current.Changed + (entry.ChangedFiles > 0 ? 1 : 0);
			_summaries[entry.ParentSessionId] = (active, awaiting, changed);
		}

		return _summaries;
	}

	private static readonly Dictionary<string, (int Active, int Awaiting, int Changed)> EmptySummaries =
		new(StringComparer.Ordinal);

	/// 命令面板的顺序：等回答的先（有人在等用户），其次在跑，其余按名字。
	/// 快照在小数组上排，不复制进额外结构。
	internal Entry[] Entries()
	{
		if (_bySession.Count == 0)
		{
			return [];
		}

		var entries = new Entry[_bySession.Count];
		_bySession.Values.CopyTo(entries, 0);
		Array.Sort(entries, static (left, right) =>
		{
			var leftPriority = Priority(left);
			var rightPriority = Priority(right);
			if (leftPriority != rightPriority)
			{
				return leftPriority.CompareTo(rightPriority);
			}

			return string.CompareOrdinal(left.Name, right.Name);
		});
		return entries;

		static int Priority(Entry entry) => entry.Awaiting || entry.Question ? 0 : entry.IsActive ? 1 : 2;
	}
}
