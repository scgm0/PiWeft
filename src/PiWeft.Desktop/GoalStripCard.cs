using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent;

namespace PiWeft.Desktop;

/// 输入区上方的目标条：一行给出阶段与轮次、目标全文，行尾是暂停 / 恢复 / 清除。
/// 与 PiDeck 的 SessionGoalStrip 同形：完成或无目标时整条不占位；阻塞时阶段名上标原因。
/// 目标条自己读 <see cref="CodingAgentSession.Goal"/>，不解析 widget 文本：桌面的状态比行格式更可信。
internal sealed class GoalStripCard
{
	/// 目标条随每次 widget 推送与渲染刷新：同一份状态重复写入会触发无谓的布局失效。
	private GoalState? _rendered;
	private readonly TextBlock _phase = new();
	private readonly TextBlock _objective = new();
	private readonly Button _pause;
	private readonly Button _resume;
	private readonly Button _clear;

	public GoalStripCard(Action pause, Action resume, Action clear)
	{
		var icon = AppIcons.Icon("target", AppStyles.RoleSubtle, 14)
			.Margin(new Thickness(0, 0, AppTheme.Space2 + 2, 0))
			.VerticalAlignment(VerticalAlignment.Center);

		_phase.FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2 + 2, 0));

		_objective.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));

		_pause = ActionButton("pause", Strings.GoalPause, pause);
		_resume = ActionButton("play", Strings.GoalResume, resume);
		_clear = ActionButton("trash", Strings.GoalClear, clear);

		var actions = new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_pause, _resume, _clear);

		View = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space1, 0))
			.IsVisible(false)
			.Child(new Grid()
				.Columns("Auto,Auto,*,Auto")
				.MinHeight(AppTheme.ControlHeightLg)
				.Children(
					icon.Column(0),
					_phase.Column(1),
					_objective.Column(2),
					actions.Column(3)));
	}

	public Border View { get; }

	internal bool IsShown => View.IsVisible;

	internal string PhaseTextForTests => _phase.Text;

	internal string ObjectiveTextForTests => _objective.Text;

	/// 写入一份目标状态；与上一次相同时不动界面。
	/// 会话每次改目标都换一个新实例，引用相等就够，不必逐字段比较整段目标文本。
	public void Render(GoalState goal)
	{
		if (ReferenceEquals(_rendered, goal))
		{
			return;
		}

		_rendered = goal;
		// 完成态不占位（PiDeck 同义）：完成消息与 toast 已经在时间线上说过一次。
		var visible = goal.Objective.Length > 0 && goal.Phase != GoalState.Complete;
		View.IsVisible(visible);
		if (!visible)
		{
			return;
		}

		var phase = Strings.GoalPhase(goal.Phase);
		_phase.Text(Strings.GoalProgress(phase, goal.RoundsStarted, goal.MaxRounds));
		if (goal.Phase == GoalState.Blocked && goal.BlockReason is { Length: > 0 } reason)
		{
			_phase.HoverTip(reason);
		}
		else
		{
			_phase.HoverTip(null);
		}

		_objective.Text(goal.Objective);
		_objective.HoverTip(goal.Objective);
		_pause.IsVisible(goal.Phase == GoalState.Active);
		// 阻塞也给恢复：只能清除的话，条上就没有纠错入口。
		_resume.IsVisible(goal.Phase is GoalState.Paused or GoalState.Blocked);
	}

	private static Button ActionButton(string icon, string tip, Action onClick) =>
		AppIcons.Button(icon, tip, onClick, size: 14)
			.Width(AppTheme.ControlHeightSm)
			.Height(AppTheme.ControlHeightSm);
}
