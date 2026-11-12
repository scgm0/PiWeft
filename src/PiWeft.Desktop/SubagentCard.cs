using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 输入区上方的「子代理」卡：列出本会话开出的子代理，点行打开子会话标签，运行中的可以停。
/// 子代理每开始一个工具都会推一次状态，只有「结构」变了才重建行，否则只改那一行右侧的文字。
internal sealed class SubagentCard
{
	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();
	// 每一行对应哪个子代理；用于判断这次刷新是重建还是只改文字。
	private readonly List<RowState> _rendered = [];

	private sealed record RowState(string Name, string SessionId, SubagentState State, bool Restored, TextBlock Meta);

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

	public void Render(
		SubagentHub? hub,
		Action<string> openChild,
		Func<string, bool> stopChild,
		Action<string> failed)
	{
		if (hub is null)
		{
			_card.Show(false);
			return;
		}

		var runs = hub.Runs;
		if (runs.Count == 0)
		{
			_card.Show(false);
			return;
		}

		var active = 0;
		foreach (var run in runs)
		{
			if (run.State is SubagentState.Starting or SubagentState.Running)
			{
				active++;
			}
		}

		_card.Meta = Strings.SubagentCardMeta(active, runs.Count);
		_card.Show(true);

		// 只在结构变化时重建：活动变化（当前工具）只改文字，保住 hover 提示与卡内焦点。
		if (SameStructure(runs))
		{
			for (var index = 0; index < runs.Count; index++)
			{
				var row = _rendered[index];
				SetMeta(row.Meta, runs[index]);
			}

			return;
		}

		_rendered.Clear();
		_rows.Clear();
		foreach (var run in runs)
		{
			var (row, meta) = Row(run, openChild, stopChild, failed);
			_rendered.Add(new RowState(run.Name, run.SessionId, run.State, run.Restored, meta));
			_rows.Children(row);
		}
	}

	/// 行数、名字、会话 id、状态、是否历史项都没变：可以只更新文字。
	private bool SameStructure(IReadOnlyList<SubagentRun> runs)
	{
		if (runs.Count != _rendered.Count)
		{
			return false;
		}

		for (var index = 0; index < runs.Count; index++)
		{
			var run = runs[index];
			var row = _rendered[index];
			if (run.State != row.State || run.Restored != row.Restored ||
				!string.Equals(run.Name, row.Name, StringComparison.Ordinal) ||
				!string.Equals(run.SessionId, row.SessionId, StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	private static void SetMeta(TextBlock meta, SubagentRun run)
	{
		var text = MetaText(run);
		if (!string.Equals(meta.Text, text, StringComparison.Ordinal))
		{
			meta.Text(text);
		}
	}

	/// 运行中加一句「在干什么」：只有状态与轮数时，长任务看着像卡住了。
	/// 历史项标出「可继续」：发一条消息就会从会话文件把它重新打开。
	private static string MetaText(SubagentRun run)
	{
		var phase = run.Restored ? Strings.SubagentRestoredState : Strings.SubagentStateText(run.State);
		if (run.Activity is { Length: > 0 } activity)
		{
			return $"{phase} · {activity}";
		}

		var meta = $"{phase} · {Strings.LinkedTurns(run.Turns)}";
		if (run.Restored && run.SessionFile is { Length: > 0 })
		{
			meta += $" · {Strings.SubagentResumable}";
		}

		if (run.Tokens > 0)
		{
			meta += $" · {TrajectoryFormat.CompactTokens(run.Tokens)} tk";
		}

		if (run.IsTerminal && run.FinishedAt > run.StartedAt)
		{
			meta += $" · {Strings.SubagentElapsed(run.FinishedAt - run.StartedAt)}";
		}

		return meta;
	}

	private static (Border Row, TextBlock Meta) Row(
		SubagentRun run,
		Action<string> openChild,
		Func<string, bool> stopChild,
		Action<string> failed)
	{
		var icon = AppIcons.Icon(
			"sparkles",
			run.Restored ? AppStyles.RoleFaint : run.State switch
			{
				SubagentState.Running => AppStyles.RoleInfo,
				SubagentState.Idle => AppStyles.RoleSuccess,
				SubagentState.Failed => AppStyles.RoleError,
				SubagentState.Starting => AppStyles.RoleWarning,
				_ => AppStyles.RoleSubtle,
			},
			13);
		var title = new TextBlock()
			.Text(run.Name)
			.FontSize(AppTheme.FontSizeCaption)
			.PrimaryText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		var meta = new TextBlock()
			.Text(MetaText(run))
			.FontSize(AppTheme.FontSizeMicro)
			.Role(!run.Restored && run.State == SubagentState.Failed ? AppStyles.RoleError : AppStyles.RoleSubtle)
			.VerticalAlignment(VerticalAlignment.Center);
		var open = AppIcons.Icon("external-link", AppStyles.RoleFaint, 12)
			.VerticalAlignment(VerticalAlignment.Center);

		var openButton = new Button()
			.Ghost()
			.IsEnabled(run.SessionId.Length > 0)
			.Content(new Grid()
				.Columns("Auto,*,Auto,Auto")
				.Spacing(6)
				.Children(icon.Column(0), title.Column(1), meta.Column(2), open.Column(3)))
			.OnClick(() => openChild(run.SessionId))
			.HoverTip(run.Result is { Length: > 0 } result
				? $"{run.Agent}：{Preview(result)}"
				: $"{run.Agent}：{run.Label}")
			.HorizontalAlignment(HorizontalAlignment.Stretch);

		var grid = new Grid()
			.Columns("*,Auto")
			.Spacing(4)
			.Children(openButton.Column(0));

		if (run.State is SubagentState.Starting or SubagentState.Running)
		{
			grid.Children(
				AppIcons.Button("square-slash", Strings.SubagentStop, () =>
					{
						if (!stopChild(run.Name))
						{
							failed(Strings.SubagentStopFailed(run.Name));
						}
					})
					.Column(1)
					.VerticalAlignment(VerticalAlignment.Center));
		}

		return (new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Child(grid), meta);
	}

	private static string Preview(string text) =>
		text.Length <= 400 ? text : string.Concat(text.AsSpan(0, 400), "…");
}
