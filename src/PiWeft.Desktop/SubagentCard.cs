using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 输入区上方的「子代理」卡：列出本会话开出的子代理，点行打开子会话标签，运行中的可以停。
/// 每行可展开：任务、进度、最近回复与用量都在卡里看得到，不必先切到子会话标签。
/// 子代理每开始一个工具都会推一次状态，只有「结构」变了才重建行，否则只改那一行右侧的文字。
internal sealed class SubagentCard
{
	private const int BriefPreviewChars = 600;
	private const int ResultPreviewChars = 1_200;
	// 卡片是快速状态视图，不是完整档案：只画最近的结束项，避免长会话把整棵 UI 拖重。
	// 更早的运行仍在 hub、侧栏与 list_subagents 里，功能不丢。
	private const int MaxDisplayedRuns = 20;

	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();
	// 每一行对应哪个子代理；用于判断这次刷新是重建还是只改文字。
	private readonly List<RowState> _rendered = [];
	// 展开状态按名字记：状态变化会让整行重建，展开的内容不能跟着合上。
	private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
	private SubagentHub? _hub;
	private Action<string>? _openChild;
	private Func<string, bool>? _stopChild;
	private Action<string>? _failed;

	private sealed record RowState(
		string Name,
		string SessionId,
		string SessionFile,
		string Model,
		string Thinking,
		SubagentState State,
		bool Restored,
		bool Awaiting,
		long Tokens,
		double Cost,
		string Role,
		TextBlock Status,
		LiveDurationText Duration,
		TextBlock Usage,
		TextBlock? Progress);

	public SubagentCard()
	{
		_rows.Spacing(6);
		_card = new ComposerWidgetCard("sparkles", Strings.SubagentCardTitle);
		_card.Body.Children(new ScrollViewer()
			.Content(_rows)
			.AutoVerticalScroll()
			.NoHorizontalScroll()
			.Padding(new Thickness(0, 0, AppTheme.Space2, 0))
			.MaxHeight(SessionTab.ComposerWidgetListMaxHeight));
	}

	public Border View => _card.View;

	internal bool IsShown => View.IsVisible;

	internal int RowCount => _rows.Children.Count;

	/// 这一帧渲染出的子代理里是否有在等用户回答；页签圆点直接用这个快照，不再扫一遍 hub。
	internal bool HasAwaiting { get; private set; }

	/// 测试用：展开或收起某一行的详情。
	internal void ToggleExpandedForTests(string name)
	{
		Toggle(name);
	}

	public void Render(
		SubagentHub? hub,
		Action<string> openChild,
		Func<string, bool> stopChild,
		Action<string> failed)
	{
		_hub = hub;
		_openChild = openChild;
		_stopChild = stopChild;
		_failed = failed;
		Refresh(force: false);
	}

	private void Refresh(bool force)
	{
		if (_hub is not { } hub)
		{
			HasAwaiting = false;
			_card.Show(false);
			return;
		}

		var runs = hub.RunsSnapshot();
		if (runs.Length == 0)
		{
			HasAwaiting = false;
			_card.Show(false);
			_rows.Clear();
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

			awaiting |= run.AwaitingUser;
		}

		HasAwaiting = awaiting;

		_card.Show(true);

		var ordered = Ordered(runs);
		var displayCount = Math.Min(ordered.Length, MaxDisplayedRuns);
		_card.Meta = Strings.SubagentCardMeta(active, runs.Length, displayCount);
		// 只在结构变化时重建：活动变化（当前工具）只改文字，保住 hover 提示与卡内焦点。
		if (!force && SameStructure(ordered, displayCount))
		{
			for (var index = 0; index < displayCount; index++)
			{
				_rendered[index] = SetMeta(_rendered[index], ordered[index]);
			}

			return;
		}

		_rendered.Clear();
		_rows.Clear();
		for (var index = 0; index < displayCount; index++)
		{
			var (row, meta) = Row(ordered[index]);
			_rendered.Add(meta);
			_rows.Add(row);
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

	/// 行数、名字、会话 id、状态、是否历史项都没变：可以只更新文字。
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
				!string.Equals(run.Name, row.Name, StringComparison.Ordinal) ||
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

	private static RowState SetMeta(RowState row, SubagentRun run)
	{
		var status = StatusText(run);
		if (!string.Equals(row.Status.Text, status, StringComparison.Ordinal))
		{
			row.Status.Text(status);
		}

		var role = StatusRole(run);
		if (!string.Equals(row.Role, role, StringComparison.Ordinal))
		{
			row.Status.Role(role);
			row = row with { Role = role };
		}

		row.Duration.SetSpan(run.StartedAt, EndedAt(run));
		row.Duration.IsVisible(run.StartedAt > 0);
		if (row.Tokens != run.Tokens || row.Cost != run.Cost)
		{
			var usage = Strings.SubagentUsage(run.Tokens, run.Cost);
			if (!string.Equals(row.Usage.Text, usage, StringComparison.Ordinal))
			{
				row.Usage.Text(usage);
			}

			row.Usage.IsVisible(usage.Length > 0);
			row = row with { Tokens = run.Tokens, Cost = run.Cost };
		}

		if (row.Progress is { } progress)
		{
			var text = run.Progress is { Length: > 0 } value
				? $"{Strings.SubagentProgress}：{value}"
				: string.Empty;
			if (!string.Equals(progress.Text, text, StringComparison.Ordinal))
			{
				progress.Text(text);
			}

			progress.IsVisible(text.Length > 0);
		}

		return row;
	}

	/// 运行中加一句「在干什么」：只有状态与轮数时，长任务看着像卡住了。
	/// 历史项仍显示最后状态；能不能继续由「可继续」标出来。
	private static string StatusText(SubagentRun run)
	{
		if (run.AwaitingUser)
		{
			return Strings.SubagentAwaitingState;
		}

		var phase = Strings.SubagentStateText(run.State);
		if (run.Activity is { Length: > 0 } activity)
		{
			return $"{phase} · {activity}";
		}

		if (run.Progress is { Length: > 0 } progress)
		{
			return $"{phase} · {progress}";
		}

		var meta = $"{phase} · {Strings.LinkedTurns(run.Turns)}";
		if (run.Restored && run.SessionFile is { Length: > 0 })
		{
			meta += $" · {Strings.SubagentResumable}";
		}

		return meta;
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

	private (Border Row, RowState Meta) Row(SubagentRun run)
	{
		var expanded = _expanded.Contains(run.Name);
		var icon = AppIcons.Icon(
			"sparkles",
			run.Restored ? AppStyles.RoleFaint : StatusRole(run),
			13);

		var name = new TextBlock()
			.Text(run.Name)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var label = new TextBlock()
			.Text(run.Label)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var title = new Grid()
			.Columns("Auto,*")
			.Spacing(6)
			.Children(name.Column(0), label.Column(1));

		var status = new TextBlock()
			.Text(StatusText(run))
			.FontSize(AppTheme.FontSizeMicro)
			.Role(StatusRole(run))
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var info = new StackPanel()
			.Spacing(1)
			.Children(title, status);

		var duration = new LiveDurationText(run.StartedAt, EndedAt(run))
		{
			FontFamily = AppTheme.MonoFont,
			FontSize = AppTheme.FontSizeMicro,
			HorizontalAlignment = HorizontalAlignment.Right,
		};
		duration.SubtleText();
		duration.IsVisible(run.StartedAt > 0);
		var usageText = Strings.SubagentUsage(run.Tokens, run.Cost);
		var usage = new TextBlock()
			.Text(usageText)
			.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.HorizontalAlignment(HorizontalAlignment.Right)
			.VerticalAlignment(VerticalAlignment.Center);
		usage.IsVisible(usageText.Length > 0);
		var stats = new StackPanel()
			.Spacing(1)
			.HorizontalAlignment(HorizontalAlignment.Right)
			.Children(duration, usage);

		var chevron = new GlyphElement()
			.Kind(expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);

		var header = new Button()
			.Ghost()
			.Padding(new Thickness(0))
			.Content(new Grid()
				.Columns("Auto,*,Auto,Auto")
				.Spacing(8)
				.Children(icon.Column(0), info.Column(1), stats.Column(2), chevron.Column(3)))
			.OnClick(() => Toggle(run.Name))
			.HoverTip(run.Result is { Length: > 0 } result ? $"{run.Agent}：{Preview(result)}" : $"{run.Agent}：{run.Label}")
			.HorizontalAlignment(HorizontalAlignment.Stretch);

		var grid = new Grid()
			.Columns("*,Auto,Auto")
			.Spacing(4)
			.Children(header.Column(0));

		if (run.SessionId.Length > 0)
		{
			grid.Children(AppIcons.Button("external-link", Strings.LinkedOpen, () => _openChild?.Invoke(run.SessionId), size: 12)
				.Column(1)
				.VerticalAlignment(VerticalAlignment.Center));
		}

		if (run.IsActive)
		{
			grid.Children(
				AppIcons.Button("square-slash", Strings.SubagentStop, () =>
					{
						if (_stopChild is not { } stop || !stop(run.Name))
						{
							_failed?.Invoke(Strings.SubagentStopFailed(run.Name));
						}
					})
					.Column(2)
					.VerticalAlignment(VerticalAlignment.Center));
		}

		var body = new List<UIElement>(5);
		var metaLine = MetaLine(run);
		if (metaLine.Length > 0)
		{
			body.Add(new TextBlock()
				.Text(metaLine)
				.FontSize(AppTheme.FontSizeMicro)
				.FontFamily(AppTheme.MonoFont)
				.SubtleText()
				.TextWrapping(TextWrapping.Wrap));
		}

		if (run.AwaitingUser)
		{
			body.Add(new Border()
				.StyleName(AppStyles.CardMuted)
				.CornerRadius(AppTheme.RadiusSm)
				.Padding(new Thickness(AppTheme.Space2))
				.Child(new TextBlock()
					.Text(Strings.SubagentAwaitingHint)
					.FontSize(AppTheme.FontSizeMicro)
					.Role(AppStyles.RoleWarning)
					.TextWrapping(TextWrapping.Wrap)));
		}

		TextBlock? progress = null;
		if (expanded)
		{
			progress = new TextBlock()
				.Text(run.Progress is { Length: > 0 } value ? $"{Strings.SubagentProgress}：{value}" : string.Empty)
				.FontSize(AppTheme.FontSizeMicro)
				.MutedText()
				.TextWrapping(TextWrapping.Wrap)
				.IsVisible(run.Progress is { Length: > 0 });
			body.Add(progress);

			if (run.Brief is { Length: > 0 } brief)
			{
				body.Add(Section(Strings.SubagentBrief, Clip(brief, BriefPreviewChars)));
			}

			if (run.Result is { Length: > 0 } resultText)
			{
				body.Add(Section(Strings.SubagentResult, Clip(resultText, ResultPreviewChars)));
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
		}

		var stack = new StackPanel()
			.Spacing(4)
			.Children([grid, .. body]);

		var row = new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space1, AppTheme.Space1, AppTheme.Space1, AppTheme.Space1))
			.Child(stack);

		return (row, new RowState(
			run.Name,
			run.SessionId,
			run.SessionFile ?? string.Empty,
			run.Model,
			run.Thinking,
			run.State,
			run.Restored,
			run.AwaitingUser,
			run.Tokens,
			run.Cost,
			StatusRole(run),
			status,
			duration,
			usage,
			progress));
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

	private static string MetaLine(SubagentRun run)
	{
		var parts = new List<string>(4);
		parts.Add(run.Agent);
		if (run.Model.Length > 0)
		{
			parts.Add(run.Model);
		}

		if (run.Thinking.Length > 0 && !string.Equals(run.Thinking, "off", StringComparison.Ordinal))
		{
			parts.Add(run.Thinking);
		}

		if (EndedAt(run) is { } endedAt && endedAt > run.StartedAt)
		{
			parts.Add(Strings.SubagentElapsed(endedAt - run.StartedAt));
		}

		return string.Join(" · ", parts);
	}

	private static Border Section(string label, string text) => new Border()
		.StyleName(AppStyles.CardMuted)
		.CornerRadius(AppTheme.RadiusSm)
		.Padding(new Thickness(AppTheme.Space2))
		.Child(new StackPanel()
			.Spacing(2)
			.Children(
				new TextBlock()
					.Text(label)
					.FontSize(AppTheme.FontSizeMicro)
					.SubtleText(),
				new TextBlock()
					.Text(text)
					.FontSize(AppTheme.FontSizeCaption)
					.MutedText()
					.TextWrapping(TextWrapping.Wrap)));

	private void Toggle(string name)
	{
		if (!_expanded.Add(name))
		{
			_expanded.Remove(name);
		}

		Refresh(force: true);
	}

	private static string Preview(string text) =>
		text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 400), "…");

	private static string Clip(string text, int max) =>
		text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}
