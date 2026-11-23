using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai.Json;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 隔离副本的两个操作：应用到主工作区，或删掉副本与未应用的改动。
internal enum WorktreeAction
{
	Apply,
	Discard,
}

/// 输入区上方的「子代理」卡：列出本会话开出的子代理，点行展开详情，行尾的打开键切到子会话标签，
/// 运行中的可以停。每行左侧是状态图标，名字与一句任务之后是右侧的耗时与用量；
/// 任务全文、进度与最近回复在展开区里看得到，不必先切到子会话标签。
/// 子代理每开始一个工具都会推一次状态，只有「结构」变了才重建行，否则只改那一行的文字。
internal sealed class SubagentCard
{
	private const int BriefPreviewChars = 600;
	private const int ResultPreviewChars = 1_200;
	// 验证输出在展开区里给一段尾巴：失败信息几乎总在最后几行。
	private const int VerifyOutputPreviewChars = 800;
	// 失败原因只留一行：卡在输入框上方，整段铺开会把别的子代理挤出视口。
	private const int FailurePreviewChars = 160;
	// 展开区从名字下面起排：8 头部内边距 + 14 状态图标 + 6 间距。
	private const double BodyIndent = 28;
	// 卡片是快速状态视图，不是完整档案：只画最近的结束项，避免长会话把整棵 UI 拖重。
	// 更早的运行仍在 hub、侧栏与 list_subagents 里，功能不丢。
	private const int MaxDisplayedRuns = 20;

	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();
	// 每一行对应哪个子代理；用于判断这次刷新是重建还是只改文字。
	private readonly List<RowState> _rendered = [];
	// 展开状态按名字记：状态变化会让整行重建，展开的内容不能跟着合上。
	private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
	// 输入框草稿也按名字记：轮次结束会重建行，打了一半的消息不能丢。
	// 问题框与自由输入框分开存：问题被父会话取走后，半截回答不该变成普通消息。
	private readonly Dictionary<(string Name, bool Answer), string> _drafts = new();
	// 回调在标签构造时给一次：刷新不带参数，调用点就不会每次建闭包与委托。
	private readonly Action<string> _openChild;
	private readonly Func<string, bool> _stopChild;
	private readonly Action<string> _failed;
	private readonly Action<SubagentRun, WorktreeAction>? _worktreeAction;
	private readonly Action<SubagentRun>? _viewWorktreeDiff;
	// 行数据来源：真实会话取 hub 快照；--ui-smoke 直接给一组固定快照，截图不受回合节奏影响。
	private SubagentHub? _hub;
	private SubagentRun[]? _smokeRuns;
	// 卡头三个数是每次刷新的固定开销：没变就不重新拼字符串。
	private (int Active, int Total, int Shown) _meta = (-1, -1, -1);

	private sealed record RowState(
		string Name,
		string SessionId,
		string SessionFile,
		string Agent,
		string Label,
		string Model,
		string Thinking,
		SubagentState State,
		bool Restored,
		bool Awaiting,
		int Turns,
		int ChangedFiles,
		bool MayWrite,
		string Worktree,
		string WorktreeBranch,
		long MaxTokens,
		int VerifyCount,
		string StopReason,
		string Question,
		bool Expanded,
		TextBlock Status,
		TextBlock Resumable,
		TextBlock Identity,
		LiveActivityText Activity,
		TextBlock Failure,
		LiveDurationText Duration,
		TextBlock Usage,
		TextBlock? Progress,
		TextBlock? Tools,
		LiveState Live);

	// 每个工具起止都会变的源值放可变对象：每次刷新原地改，
	// 不用 record with 复制整行（结构字段与控制引用占了二十多项）。
	private sealed class LiveState(long tokens, double cost, string? activity, long lastActivityAt, int toolCalls, string? progress, string? result)
	{
		public long Tokens { get; set; } = tokens;
		public double Cost { get; set; } = cost;
		public string? ActivityValue { get; set; } = activity;
		public long LastActivityAt { get; set; } = lastActivityAt;
		public int ToolCalls { get; set; } = toolCalls;
		public string? ProgressValue { get; set; } = progress;
		public string? ResultValue { get; set; } = result;
	}

	public SubagentCard(
		Action<string> openChild,
		Func<string, bool> stopChild,
		Action<string> failed,
		Action<SubagentRun, WorktreeAction>? worktreeAction = null,
		Action<SubagentRun>? viewWorktreeDiff = null)
	{
		// 回调在标签构造时给一次：读屏每有工具起止就刷新，逐个参数传会在调用点建闭包与委托。
		_openChild = openChild;
		_stopChild = stopChild;
		_failed = failed;
		_worktreeAction = worktreeAction;
		_viewWorktreeDiff = viewWorktreeDiff;
		_rows.Spacing(6);
		_card = new ComposerWidgetCard("sparkles", Strings.SubagentCardTitle);
		_card.Body.Children(_rows);
	}

	public Border View => _card.View;

	internal bool IsShown => View.IsVisible;

	internal void Expand() => _card.IsExpanded = true;

	internal void ShowHeader(bool visible) => _card.ShowHeader(visible);

	/// 工作台摘要用的计数文本。
	internal string Meta => _card.Meta;

	internal int RowCount => _rows.Children.Count;

	/// 这一帧渲染出的子代理里是否有在等用户回答；页签圆点直接用这个快照，不再扫一遍 hub。
	internal bool HasAwaiting { get; private set; }

	/// 测试用：展开或收起某一行的详情。
	internal void ToggleExpandedForTests(string name)
	{
		Toggle(name);
	}

	public void Render(SubagentHub? hub)
	{
		_hub = hub;
		_smokeRuns = null;
		Refresh(force: false);
	}

	/// 仅供 <c>--ui-smoke subagents</c>：行直接吃这组快照，不建子会话。
	internal void RenderRunsForSmoke(SubagentRun[] runs)
	{
		_hub = null;
		// Ordered 会原地排序；复制一份，别把调用方的 fixture 顺序改掉。
		_smokeRuns = [.. runs];
		Refresh(force: true);
	}

	private void Refresh(bool force)
	{
		if (_hub is null && _smokeRuns is null)
		{
			HasAwaiting = false;
			_card.Show(false);
			return;
		}

		var runs = _smokeRuns ?? _hub!.RunsSnapshot();
		if (runs.Length == 0)
		{
			HasAwaiting = false;
			_card.Show(false);
			ClearRows();
			_rendered.Clear();
			_expanded.Clear();
			return;
		}

		var active = 0;
		var awaiting = false;
		foreach (var run in runs)
		{
			if (run.IsActive)
			{
				active++;
			}

			// 等父会话回答也算「需要你」：父会话可能没接上，20 秒后问题会落到问询卡上。
			awaiting |= run.AwaitingUser || run.Question is { Length: > 0 };
		}

		HasAwaiting = awaiting;

		_card.Show(true);

		var ordered = Ordered(runs);
		var displayCount = Math.Min(ordered.Length, MaxDisplayedRuns);
		var metaKey = (active, runs.Length, displayCount);
		if (metaKey != _meta)
		{
			_meta = metaKey;
			_card.Meta = Strings.SubagentCardMeta(active, runs.Length, displayCount);
		}

		// 只在结构变化时重建：活动变化（当前工具）只改文字，保住 hover 提示与卡内焦点。
		if (!force && SameStructure(ordered, displayCount))
		{
			for (var index = 0; index < displayCount; index++)
			{
				SetMeta(_rendered[index], ordered[index]);
			}

			return;
		}

		_rendered.Clear();
		ClearRows();
		for (var index = 0; index < displayCount; index++)
		{
			var (row, meta) = Row(ordered[index]);
			_rendered.Add(meta);
			_rows.Add(row);
		}
	}

	/// 行里的 <see cref="LiveDurationText"/> 自己跑 100 ms 定时器：只摘不释放会把表留到进程结束。
	/// 重建只发生在状态变化时，逐个 Remove 的挪动成本远小于泄漏。
	private void ClearRows()
	{
		while (_rows.Children.Count > 0)
		{
			var row = _rows.Children[^1];
			_rows.Remove(row);
			UiLifetime.Release(row);
		}
	}

	/// 运行中的排在前面，其余按结束时间倒序：刚结束的一轮不用翻到列表底部才看见。
	private static SubagentRun[] Ordered(SubagentRun[] runs)
	{
		Array.Sort(runs, static (left, right) =>
		{
			var leftActive = left.IsActive;
			var rightActive = right.IsActive;
			if (leftActive != rightActive)
			{
				return leftActive ? -1 : 1;
			}

			if (!leftActive && left.FinishedAt != right.FinishedAt)
			{
				return right.FinishedAt.CompareTo(left.FinishedAt);
			}

			// 毫秒级时间戳会打平；用唯一名字兜底，排在相同输入下保持稳定，避免每次刷新换行。
			var started = left.StartedAt.CompareTo(right.StartedAt);
			return started != 0 ? started : string.CompareOrdinal(left.Name, right.Name);
		});
		return runs;
	}

	/// 行数、名字、会话 id、状态、轮数、角色、模型都没变：可以只更新计时与文本块。
	/// 状态字、身份行、「可继续」与失败原因只依赖这些字段，所以它们只在重建时写一次。
	private bool SameStructure(SubagentRun[] runs, int count)
	{
		if (count != _rendered.Count)
		{
			return false;
		}

		for (var index = 0; index < count; index++)
		{
			var run = runs[index];
			var row = _rendered[index];
			if (run.State != row.State || run.Restored != row.Restored || run.AwaitingUser != row.Awaiting ||
				run.Turns != row.Turns || run.MaxTokens != row.MaxTokens ||
				run.ChangedFiles.Count != row.ChangedFiles || run.MayWrite != row.MayWrite ||
				!string.Equals(run.WorktreePath ?? string.Empty, row.Worktree, StringComparison.Ordinal) ||
				!string.Equals(run.WorktreeBranch ?? string.Empty, row.WorktreeBranch, StringComparison.Ordinal) ||
				run.Verify.Count != row.VerifyCount ||
				!string.Equals(run.Question ?? string.Empty, row.Question, StringComparison.Ordinal) ||
				!string.Equals(run.StopReason, row.StopReason, StringComparison.Ordinal) ||
				!string.Equals(run.Name, row.Name, StringComparison.Ordinal) ||
				!string.Equals(run.Agent, row.Agent, StringComparison.Ordinal) ||
				!string.Equals(run.Label, row.Label, StringComparison.Ordinal) ||
				!string.Equals(run.SessionId, row.SessionId, StringComparison.Ordinal) ||
				!string.Equals(run.SessionFile ?? string.Empty, row.SessionFile, StringComparison.Ordinal) ||
				!string.Equals(run.Model, row.Model, StringComparison.Ordinal) ||
				!string.Equals(run.Thinking, row.Thinking, StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	/// 结构没变时只写会变的那几样：当前工具、进度、用量、失败原因、秒表。
	/// 每次都先比源值，字符串只在真变了才拼；变化路径不复制整行，只有文本格式化本身会分配。
	private static void SetMeta(RowState row, SubagentRun run)
	{
		row.Duration.SetSpan(run.StartedAt, EndedAt(run));
		var hasDuration = run.StartedAt > 0;
		if (row.Duration.IsVisible != hasDuration)
		{
			row.Duration.IsVisible(hasDuration);
		}

		var live = row.Live;
		if (live.Tokens != run.Tokens || live.Cost != run.Cost)
		{
			SetText(row.Usage, Strings.SubagentUsage(run.Tokens, run.Cost, run.MaxTokens));
			live.Tokens = run.Tokens;
			live.Cost = run.Cost;
		}

		if (!string.Equals(live.ActivityValue, run.Activity, StringComparison.Ordinal) ||
			live.LastActivityAt != run.LastActivityAt)
		{
			row.Activity.SetActivity(ActivityText(run), run.LastActivityAt, run.IsActive && !run.AwaitingUser);
			live.ActivityValue = run.Activity;
			live.LastActivityAt = run.LastActivityAt;
		}

		if (row.Progress is { } progress &&
			!string.Equals(live.ProgressValue, run.Progress, StringComparison.Ordinal))
		{
			SetText(progress, run.Progress is { Length: > 0 } value ? $"{Strings.SubagentProgress}：{value}" : string.Empty);
			live.ProgressValue = run.Progress;
		}

		if (row.Tools is { } tools && live.ToolCalls != run.ToolCalls)
		{
			SetText(tools, run.ToolCalls > 0 ? Strings.SubagentToolCalls(run.ToolCalls) : string.Empty);
			live.ToolCalls = run.ToolCalls;
		}

		if (!ReferenceEquals(live.ResultValue, run.Result))
		{
			SetText(row.Failure, FailurePreview(run, row.Expanded));
			live.ResultValue = run.Result;
		}
	}

	/// 写文本顺手同步可见性：空串的行不该占位，而每次刷新重复写同一个值只会白跑一遍布局脏标记。
	private static void SetText(TextBlock block, string text)
	{
		if (string.Equals(block.Text, text, StringComparison.Ordinal))
		{
			return;
		}

		block.Text(text);
		block.IsVisible(text.Length > 0);
	}

	/// 状态字只留状态与轮数：当前工具与进度各占一行，挤进同一句只会互相挤掉。
	/// 「可继续」是能力不是状态，单独上弱色，不跟着失败一起变红。
	private static string StatusText(SubagentRun run)
	{
		if (run.AwaitingUser)
		{
			return Strings.SubagentAwaitingState;
		}

		if (run.Question is { Length: > 0 })
		{
			// 父会话可能自己回答；超时后卡片会换成用户可答的问询卡。
			return Strings.SubagentQuestionState;
		}

		var phase = Strings.SubagentStateText(run.State);
		return run.Turns > 0 ? $"{phase} · {Strings.LinkedTurns(run.Turns)}" : phase;
	}

	private static string ResumableText(SubagentRun run) =>
		run.Restored && run.SessionFile is { Length: > 0 } ? $"· {Strings.SubagentResumable}" : string.Empty;

	/// 运行中的当前工具（<c>read src/Auth.cs</c>）；结束后清空，历史项不显示。
	private static string ActivityText(SubagentRun run) =>
		run.IsActive && !run.AwaitingUser ? run.Activity ?? string.Empty : string.Empty;

	/// 失败行的原因：展开时交给「失败原因」段，收起时只留一行。
	/// 宿主验证没过时直接报验证结论：模型那句「都改好了」不是失败原因。
	/// 直接在结果文本的 span 上截首行，不在长正文上先复制一份再裁剪。
	private static string FailurePreview(SubagentRun run, bool expanded)
	{
		if (run.State != SubagentState.Failed)
		{
			return string.Empty;
		}

		if (VerifyFailureLine(run) is { Length: > 0 } verify)
		{
			return verify;
		}

		if (expanded || run.Result is not { Length: > 0 } result)
		{
			return string.Empty;
		}

		var end = result.IndexOf('\n');
		var line = (end < 0 ? result.AsSpan() : result.AsSpan(0, end)).Trim();
		return line.Length <= FailurePreviewChars
			? line.ToString()
			: string.Concat(line[..FailurePreviewChars], "…");
	}

	/// 失败的那条验证命令；都通过或没跑验证时为空。
	private static string VerifyFailureLine(SubagentRun run)
	{
		foreach (var result in run.Verify)
		{
			if (result.Passed)
			{
				continue;
			}

			return result.TimedOut
				? Strings.SubagentVerifyTimedOut(result.Command)
				: Strings.SubagentVerifyFailed(result.Command, result.ExitCode ?? -1);
		}

		return string.Empty;
	}

	/// 非失败行的一句附注：验证通过、或回复被输出上限截断。
	/// 截断优先：结论缺尾巴比「验证通过」更该被看见。
	private static string NoteText(SubagentRun run)
	{
		if (run.State == SubagentState.Failed)
		{
			return string.Empty;
		}

		if (string.Equals(run.StopReason, "length", StringComparison.Ordinal))
		{
			return Strings.SubagentTruncated;
		}

		if (run.Verify.Count == 0)
		{
			return string.Empty;
		}

		foreach (var result in run.Verify)
		{
			if (!result.Passed)
			{
				return string.Empty;
			}
		}

		return Strings.SubagentVerifyPassed(run.Verify[0].Command);
	}

	private static string NoteRole(SubagentRun run) =>
		string.Equals(run.StopReason, "length", StringComparison.Ordinal) ? AppStyles.RoleWarning : AppStyles.RoleSuccess;

	/// 状态图标：颜色与形状都表意，文字再写一遍状态名，色弱也读得出。
	private static string StateIcon(SubagentRun run)
	{
		if (run.AwaitingUser)
		{
			return "circle-question-mark";
		}

		return run.State switch
		{
			SubagentState.Starting => "circle-dashed",
			SubagentState.Running => "loader-circle",
			SubagentState.Idle => "circle-check",
			SubagentState.Failed => "x",
			_ => "square-slash",
		};
	}

	private static string StatusRole(SubagentRun run)
	{
		if (run.AwaitingUser)
		{
			return AppStyles.RoleWarning;
		}

		return run.State switch
		{
			SubagentState.Running => AppStyles.RoleInfo,
			SubagentState.Idle => AppStyles.RoleSuccess,
			SubagentState.Failed => AppStyles.RoleError,
			SubagentState.Starting => AppStyles.RoleWarning,
			_ => AppStyles.RoleSubtle,
		};
	}

	private (UIElement Row, RowState Meta) Row(SubagentRun run)
	{
		var expanded = _expanded.Contains(run.Name);
		var role = StatusRole(run);

		// 状态图标承担颜色与形状，状态字再写一遍：色弱和截图里都读得出。
		// 不做旋转：子代理可能跑十分钟，AnimationClock 会把宿主拖进持续重绘；右侧的秒表已经在动。
		var glyph = AppIcons.Icon(StateIcon(run), role, 14);
		glyph.VerticalAlignment = VerticalAlignment.Top;
		glyph.Margin(new Thickness(0, 2, 0, 0));

		var name = new TextBlock()
			.Text(run.Name)
			.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var label = new TextBlock()
			.Text(run.Label)
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);

		var duration = new LiveDurationText(run.StartedAt, EndedAt(run))
		{
			FontFamily = AppTheme.MonoFont,
			FontSize = AppTheme.FontSizeMicro,
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		duration.SubtleText();
		if (run.IsActive)
		{
			// 运行中的秒表每 100 ms 换一次字宽；固定最小宽，右侧用量与展开键不跟着跳。
			duration.TextAlignment(TextAlignment.Right).MinWidth(44);
		}

		duration.IsVisible(run.StartedAt > 0);
		var usageText = Strings.SubagentUsage(run.Tokens, run.Cost, run.MaxTokens);
		var usage = new TextBlock()
			.Text(usageText)
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			.SubtleText()
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Center);
		usage.IsVisible(usageText.Length > 0);

		var titleLine = new Grid()
			.Columns("Auto,*,Auto,Auto")
			.Spacing(6)
			.Children(name.Column(0), label.Column(1), duration.Column(2), usage.Column(3));

		var status = new TextBlock()
			.Text(StatusText(run))
			.FontSize(AppTheme.FontSizeMicro)
			.Role(role)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var resumableText = ResumableText(run);
		var resumable = new TextBlock()
			.Text(resumableText)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		resumable.IsVisible(resumableText.Length > 0);
		var identity = new TextBlock()
			.Text(IdentityLine(run))
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			.SubtleText()
			.HorizontalAlignment(HorizontalAlignment.Right)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var statusLine = new Grid()
			.Columns("Auto,Auto,*")
			.Spacing(4)
			.Children(status.Column(0), resumable.Column(1), identity.Column(2));

		var activityText = ActivityText(run);
		var activity = new LiveActivityText()
		{
			FontSize = AppTheme.FontSizeMicro,
			FontFamily = AppTheme.MonoFont,
		};
		activity.SubtleText();
		activity.TextTrimming(TextTrimming.CharacterEllipsis);
		activity.VerticalAlignment = VerticalAlignment.Center;
		// 运行中的行每秒自己走一次时间；结束时停在最后一条工具名，不再计时。
		activity.SetActivity(activityText, run.LastActivityAt, run.IsActive && !run.AwaitingUser);

		var failureText = FailurePreview(run, expanded);
		var failure = new TextBlock()
			.Text(failureText)
			.FontSize(AppTheme.FontSizeMicro)
			.ErrorText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		failure.IsVisible(failureText.Length > 0);

		var noteText = NoteText(run);
		var note = new TextBlock()
			.Text(noteText)
			.FontSize(AppTheme.FontSizeMicro)
			.Role(NoteRole(run))
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		// 验证通过但这一轮还在跑时，详情行属于当前的 verify 命令：两句话叠在同一格里只会互相盖住。
		note.IsVisible(noteText.Length > 0 && !run.IsActive);

		var changesText = Strings.SubagentChanges(run.ChangedFiles.Count, run.MayWrite);
		var changes = new TextBlock()
			.Text(changesText)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.HorizontalAlignment(HorizontalAlignment.Right)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		changes.IsVisible(changesText.Length > 0);

		// 详情行：当前工具、失败原因、验证结论共用这一格，改动计数靠右。
		// 以前它们各占一行、空内容整行隐藏：快速连调工具时每次起止都增删一行，卡片高度跟着跳。
		// 这里行常驻，内容只在同一格里换字；两个零透明度的测量桩撑住行高（正文与等宽字体各一），
		// 有没有活动、有没有改动都不改变行高。
		var detail = new Grid()
			.Columns("*,Auto")
			.Spacing(6)
			.Children(
				new Grid()
					.Columns("*")
					.Children(
						new TextBlock().Text(" ").FontSize(AppTheme.FontSizeMicro)
							.Opacity(0).GridPosition(0, 0),
						new TextBlock().Text(" ").FontSize(AppTheme.FontSizeMicro)
							.FontFamily(AppTheme.MonoFont).Opacity(0).GridPosition(0, 0),
						activity.GridPosition(0, 0),
						failure.GridPosition(0, 0),
						note.GridPosition(0, 0))
					.GridPosition(0, 0),
				changes.GridPosition(0, 1));

		var worktreeText = run.WorktreePath is { Length: > 0 }
			? Strings.SubagentWorktree(run.WorktreeBranch)
			: string.Empty;
		var worktree = new TextBlock()
			.Text(worktreeText)
			.FontSize(AppTheme.FontSizeMicro)
			.MutedText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		worktree.IsVisible(worktreeText.Length > 0);

		// 没有详情可写的行不占这一行：已完成、没有改动也没有验证结论的行保持两行高。
		// 这些取舍只依赖 SameStructure 比较的字段，行重建时才重算，运行中不会来回变。
		var showDetail = run.IsActive && !run.AwaitingUser ||
			failureText.Length > 0 || noteText.Length > 0 || changesText.Length > 0;

		var content = new StackPanel()
			.Spacing(1)
			.Children(titleLine, statusLine);
		if (showDetail)
		{
			content.Children(detail);
		}

		content.Children(
			worktree,
			new TextBlock()
				.Text(Strings.SubagentAwaitingHint)
				.FontSize(AppTheme.FontSizeMicro)
				.Role(AppStyles.RoleWarning)
				.TextWrapping(TextWrapping.Wrap)
				.IsVisible(run.AwaitingUser));

		var header = new Button()
			.Ghost()
			.Padding(new Thickness(AppTheme.Space2, AppTheme.Space1, AppTheme.Space1, AppTheme.Space1))
			.Content(new Grid()
				.Columns("Auto,*")
				.Spacing(6)
				.Children(glyph.Column(0), content.Column(1)))
			.OnClick(() => Toggle(run.Name))
			.HoverTip(run.Result is { Length: > 0 } result ? $"{run.Agent}：{Preview(result)}" : $"{run.Agent}：{run.Label}")
			.HorizontalAlignment(HorizontalAlignment.Stretch);

		var actions = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.HorizontalAlignment(HorizontalAlignment.Right);
		if (run.WorktreePath is { Length: > 0 } worktreePath)
		{
			actions.Children(AppIcons.Button("folder-open", Strings.SubagentWorktreeOpen, () =>
				{
					try
					{
						Shell.Open(worktreePath);
					}
					catch (Exception ex)
					{
						Notify(ex.Message);
					}
				}, size: 12)
				.VerticalAlignment(VerticalAlignment.Center));
		}

		if (run.SessionId.Length > 0)
		{
			actions.Children(AppIcons.Button("external-link", Strings.LinkedOpen, () => _openChild?.Invoke(run.SessionId), size: 12)
				.VerticalAlignment(VerticalAlignment.Center));
		}

		if (run.IsActive)
		{
			actions.Children(
				AppIcons.Button("square-slash", Strings.SubagentStop, () =>
					{
						if (_stopChild is not { } stop || !stop(run.Name))
						{
							_failed?.Invoke(Strings.SubagentStopFailed(run.Name));
						}
					}, size: 12)
					.VerticalAlignment(VerticalAlignment.Center));
		}

		if (run.State == SubagentState.Failed)
		{
			// 失败行不要求先开标签再重开一轮：原会话还在文件里，follow-up 会冷恢复。
			actions.Children(
				AppIcons.Button("refresh-cw", Strings.SubagentContinue, () => Continue(run), size: 12)
					.VerticalAlignment(VerticalAlignment.Center));
		}

		var chevron = new Button()
			.Ghost()
			.Padding(new Thickness(AppTheme.Space1))
			.Content(new GlyphElement()
				.Kind(expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown)
				.GlyphSize(3)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle))
			.OnClick(() => Toggle(run.Name))
			.VerticalAlignment(VerticalAlignment.Center);

		var head = new Grid()
			.Columns("*,Auto,Auto")
			.Spacing(2)
			.Children(header.Column(0), actions.Column(1), chevron.Column(2));

		var body = new List<UIElement>(5);
		TextBlock? progress = null;
		TextBlock? tools = null;
		if (expanded)
		{
			progress = new TextBlock()
				.Text(run.Progress is { Length: > 0 } value ? $"{Strings.SubagentProgress}：{value}" : string.Empty)
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap)
				.IsVisible(run.Progress is { Length: > 0 });
			body.Add(progress);

			// 展开期间子代理还在跑：工具数会变，留一枚可写的文本给 SetMeta 更新。
			tools = new TextBlock()
				.Text(run.ToolCalls > 0 ? Strings.SubagentToolCalls(run.ToolCalls) : string.Empty)
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText()
				.IsVisible(run.ToolCalls > 0);
			body.Add(tools);

			if (run.Brief is { Length: > 0 } brief)
			{
				body.Add(Section(Strings.SubagentBrief, Clip(brief, BriefPreviewChars)));
			}

			if (run.ChangedFiles.Count > 0)
			{
				var files = string.Join("\n", run.ChangedFiles);
				body.Add(Section(Strings.SubagentChangedFiles, files, copyText: files));
			}
			else if (run.MayWrite)
			{
				body.Add(Section(Strings.SubagentChangedFiles, Strings.SubagentMayWriteHint));
			}

			if (run.Question is { Length: > 0 } question)
			{
				// 父会话可能不在这里，用户在卡片上直接答，不必等 20 秒摊到问询卡。
				body.Add(Section(Strings.SubagentQuestion, question));
				var answer = new TextBox()
					.PrimarySelection()
					.Placeholder(Strings.SubagentAnswerPlaceholder)
					.Text(_drafts.GetValueOrDefault((run.Name, true)) ?? string.Empty)
					.OnTextChanged(text => _drafts[(run.Name, true)] = text ?? string.Empty);
				body.Add(new Grid()
					.Columns("*,Auto")
					.Spacing(4)
					.Children(
						answer.Column(0),
						AppIcons.Button("send-horizontal", Strings.SubagentAnswer, () => Answer(run, answer)).Column(1)));
			}
			else
			{
				// 空闲时排下一轮，运行中在当前工具边界插入。失败行的「继续」是一键路径，
				// 这里是自由输入：追问、追范围、改变做法。
				var message = new TextBox()
					.PrimarySelection()
					.Placeholder(Strings.SubagentMessagePlaceholder)
					.Text(_drafts.GetValueOrDefault((run.Name, false)) ?? string.Empty)
					.OnTextChanged(text => _drafts[(run.Name, false)] = text ?? string.Empty);
				body.Add(new Grid()
					.Columns("*,Auto")
					.Spacing(4)
					.Children(
						message.Column(0),
						AppIcons.Button("send-horizontal", Strings.SubagentSend, () => Send(run, message)).Column(1)));
			}

			if (run.Result is { Length: > 0 } resultText)
			{
				body.Add(Section(
					run.State == SubagentState.Failed ? Strings.SubagentFailure : Strings.SubagentResult,
					Clip(resultText, ResultPreviewChars),
					copyText: resultText));
			}

			if (run.StructuredOutput is { } structured)
			{
				// 模型与 list_subagents 已经看到结构化结果；卡片展开区之前漏了它。
				var json = structured.ToJsonString(PiJson.NodeOptions);
				body.Add(Section(Strings.SubagentStructured, Clip(json, ResultPreviewChars), copyText: json));
			}

			if (run.WorktreePath is { Length: > 0 } expandedWorktree)
			{
				body.Add(Section(
					Strings.SubagentWorktreePath,
					$"{expandedWorktree}\n{run.WorktreeBranch}\n{ShortSha(run.WorktreeBase)}",
					copyText: expandedWorktree));
				if (run.IsTerminal)
				{
					var worktreeActions = new WrapPanel().Spacing(AppTheme.Space1);
					if (_viewWorktreeDiff is { } viewDiff)
					{
						worktreeActions.Children(AppIcons.TextButton(
							"file-diff",
							Strings.SubagentWorktreeDiff,
							() => viewDiff(run)));
					}

					if (_worktreeAction is { } worktreeAction)
					{
						worktreeActions.Children(
							AppIcons.TextButton("download", Strings.SubagentWorktreeApply, () => worktreeAction(run, WorktreeAction.Apply)),
							AppIcons.TextButton("trash", Strings.SubagentWorktreeDiscard, () => worktreeAction(run, WorktreeAction.Discard)));
					}

					body.Add(worktreeActions);
				}
			}

			if (run.SessionFile is { Length: > 0 } file)
			{
				body.Add(new TextBlock()
					.Text(file)
					.FontSize(AppTheme.FontSizeMicro)
					.FontFamily(AppTheme.MonoFont)
					.FaintText()
					.TextWrapping(TextWrapping.Wrap));
			}

			// 宿主跑的验证合成一段：多条命令各建一个同标题的卡片只会把展开区撑散。
			if (run.Verify.Count > 0)
			{
				var verifyBody = new StringBuilder();
				foreach (var verification in run.Verify)
				{
					if (verifyBody.Length > 0)
					{
						verifyBody.Append('\n');
					}

					verifyBody.Append(verification.TimedOut
						? Strings.SubagentVerifyTimedOut(verification.Command)
						: $"{verification.Command} · {Strings.SubagentVerifyExit(verification.ExitCode ?? -1)}");
					if (verification.Output is { Length: > 0 } output)
					{
						verifyBody.Append('\n').Append(Tail(output, VerifyOutputPreviewChars));
					}
				}

				body.Add(Section(Strings.SubagentVerifySection, verifyBody.ToString()));
			}
		}

		var stack = new StackPanel()
			.Spacing(2)
			.Children(head);
		if (body.Count > 0)
		{
			stack.Children(new StackPanel()
				.Spacing(4)
				.Margin(new Thickness(BodyIndent, 0, AppTheme.Space1, AppTheme.Space1))
				.Children([.. body]));
		}

		return (stack, new RowState(
			run.Name,
			run.SessionId,
			run.SessionFile ?? string.Empty,
			run.Agent,
			run.Label,
			run.Model,
			run.Thinking,
			run.State,
			run.Restored,
			run.AwaitingUser,
			run.Turns,
			run.ChangedFiles.Count,
			run.MayWrite,
			run.WorktreePath ?? string.Empty,
			run.WorktreeBranch ?? string.Empty,
			run.MaxTokens,
			run.Verify.Count,
			run.StopReason,
			run.Question ?? string.Empty,
			expanded,
			status,
			resumable,
			identity,
			activity,
			failure,
			duration,
			usage,
			progress,
			tools,
			new LiveState(run.Tokens, run.Cost, run.Activity, run.LastActivityAt, run.ToolCalls, run.Progress, run.Result)));
	}

	private static long? EndedAt(SubagentRun run)
	{
		if (!run.IsTerminal || run.StartedAt <= 0)
		{
			return null;
		}

		// 旧条目可能缺 finishedAt：停在 startedAt，别让已结束的行一直走秒。
		return run.FinishedAt > run.StartedAt ? run.FinishedAt : run.StartedAt;
	}

	private static string IdentityLine(SubagentRun run)
	{
		List<string> parts = [run.Agent];
		if (run.Model.Length > 0)
		{
			parts.Add(run.Model);
		}

		if (run.Thinking.Length > 0 && !string.Equals(run.Thinking, "off", StringComparison.Ordinal))
		{
			parts.Add(run.Thinking);
		}

		return string.Join(" · ", parts);
	}

	private Border Section(string label, string text, string? copyText = null)
	{
		var header = new Grid()
			.Columns("*,Auto")
			.Children(new TextBlock()
				.Text(label)
				.FontSize(AppTheme.FontSizeMicro)
				.SubtleText()
				.Column(0));
		if (copyText is { Length: > 0 } copy)
		{
			header.Children(AppIcons.Button("copy", Strings.CopyMessage, () => Copy(copy), size: 12).Column(1));
		}

		return new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2))
			.Child(new StackPanel()
				.Spacing(2)
				.Children(
					header,
					new TextBlock()
						.Text(text)
						.FontSize(AppTheme.FontSizeCaption)
						.MutedText()
						.TextWrapping(TextWrapping.Wrap)));
	}

	/// 复制走系统剪贴板；剪贴板不可用时退成一条提示，不静默吞掉。
	private void Copy(string text)
	{
		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		Toasts.Show(View, clipboard?.TrySetText(text) == true ? Strings.Copied : Strings.ClipboardUnavailable);
	}

	/// 失败行继续：follow-up 一条，不需要用户再写一遍任务。
	/// 已经回收的会话由 hub 按会话文件冷恢复。
	private void Continue(SubagentRun run)
	{
		if (_hub is not { } hub)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				var outcome = await hub.SendAsync(run.Name, Strings.SubagentContinuePrompt).ConfigureAwait(false);
				if (!outcome.Ok)
				{
					Notify(Strings.SubagentContinueFailed(outcome.Text));
				}
			}
			catch (Exception ex)
			{
				Notify(Strings.SubagentContinueFailed(ex.Message));
			}
		});
	}

	/// 卡片上回答 ask_parent 的问题；取走问题后父会话再答会按普通消息处理。
	private void Answer(SubagentRun run, TextBox input)
	{
		var answer = (input.Text ?? string.Empty).Trim();
		if (answer.Length == 0)
		{
			Notify(Strings.SubagentAnswerEmpty);
			return;
		}

		// 先清空再答：AnswerQuestion 可能同步重建行，之后再碰旧输入框不安全。
		input.Text = string.Empty;
		_drafts.Remove((run.Name, true));
		if (_hub is null || !_hub.AnswerQuestion(run.SessionId, answer))
		{
			Notify(Strings.SubagentAnswerFailed(run.Name));
		}
	}

	/// 卡片上的自由输入：运行中走 steer，空闲时排下一轮。
	private void Send(SubagentRun run, TextBox input)
	{
		var message = (input.Text ?? string.Empty).Trim();
		if (message.Length == 0)
		{
			Notify(Strings.SubagentMessageEmpty);
			return;
		}

		if (_hub is not { } hub)
		{
			return;
		}

		// 点发送就清空：后台续体不能碰控件，留着文本容易再点一次重发。
		input.Text = string.Empty;
		_drafts.Remove((run.Name, false));

		_ = Task.Run(async () =>
		{
			try
			{
				var outcome = await hub.SendAsync(run.Name, message, "auto").ConfigureAwait(false);
				if (!outcome.Ok)
				{
					Notify(Strings.SubagentSendFailed(outcome.Text));
				}
			}
			catch (Exception ex)
			{
				Notify(Strings.SubagentSendFailed(ex.Message));
			}
		});
	}

	/// 后台线程来的错误回 UI 线程：toast 要碰窗口。
	private void Notify(string message)
	{
		if (!Application.IsRunning)
		{
			return;
		}

		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			_failed?.Invoke(message);
			return;
		}

		dispatcher.BeginInvoke(() => _failed?.Invoke(message));
	}

	private void Toggle(string name)
	{
		if (!_expanded.Add(name))
		{
			_expanded.Remove(name);
		}

		RefreshRow(name);
	}

	/// 只替换被展开/收起的那一行：其他行里正在输入的 TextBox、光标和 IME 不受影响。
	/// 找不到对应快照时退回整表刷新，保证状态仍然一致。
	private void RefreshRow(string name)
	{
		var runs = _smokeRuns ?? _hub?.RunsSnapshot();
		if (runs is null)
		{
			Refresh(force: true);
			return;
		}

		Ordered(runs);
		SubagentRun? run = null;
		foreach (var candidate in runs)
		{
			if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
			{
				run = candidate;
				break;
			}
		}

		var index = _rendered.FindIndex(row => string.Equals(row.Name, name, StringComparison.Ordinal));
		if (run is null || index < 0 || index >= _rows.Children.Count)
		{
			Refresh(force: true);
			return;
		}

		var (rowView, meta) = Row(run);
		var old = _rows.Children[index];
		_rows.Remove(old);
		UiLifetime.Release(old);
		_rows.Insert(index, rowView);
		_rendered[index] = meta;
	}

	private static string Preview(string text) =>
		text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 400), "…");

	private static string ShortSha(string? sha) =>
		sha is { Length: > 8 } ? sha[..8] : sha ?? string.Empty;

	private static string Clip(string text, int max) =>
		text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");

	/// 验证输出的尾巴：失败信号总在最后几行。
	private static string Tail(string text, int max) =>
		text.Length <= max ? text : string.Concat("…", text.AsSpan(text.Length - max));
}
