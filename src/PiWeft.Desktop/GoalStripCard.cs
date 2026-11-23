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
	private long _renderedUsedBucket = -1;
	private readonly TextBlock _phase = new();
	private readonly Image _goalIcon;
	private readonly TextBlock _editStatus = new();
	private readonly TextBlock _objective = new();
	private readonly Button _edit;
	private readonly Button _pause;
	private readonly Button _resume;
	private readonly Button _extend;
	private readonly Button _clear;
	private readonly Grid _browseRow;
	private readonly StackPanel _editRow;
	private readonly StackPanel _editEditorHost = new();
	private readonly TextBlock _details;
	private readonly GlyphElement _chevron;
	private bool _expanded;
	/// 编辑器按需创建：未编辑时视觉树里只有浏览行，主输入框不会撞上第二个 MultiLineTextBox。
	private MultiLineTextBox? _editor;
	private readonly Action<string> _editAction;
	private readonly string? _cwd;
	private bool _editing;

	public GoalStripCard(Action pause, Action resume, Action clear, Action<string> edit, string? cwd = null)
	{
		_editAction = edit;
		_cwd = cwd;
		_goalIcon = AppIcons.Icon("target", AppStyles.RoleSubtle, 14)
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
			.TextWrapping(TextWrapping.NoWrap)
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(0, 0, AppTheme.Space2, 0));

		_details = new TextBlock()
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextWrapping(TextWrapping.Wrap)
			.Margin(new Thickness(AppTheme.Space2 + 14 + 6, 0, AppTheme.Space2, AppTheme.Space2))
			.IsVisible(false);
		_chevron = new GlyphElement()
			.Kind(GlyphKind.ChevronDown)
			.GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center)
			.WithTheme((theme, element) => element.Foreground = AppTheme.For(theme).TextSubtle);
		var expand = new Button()
			.Ghost()
			.Content(_chevron)
			.Padding(new Thickness(AppTheme.Space1))
			.VerticalAlignment(VerticalAlignment.Center)
			.HoverTip(Strings.GoalExpandHint)
			.OnClick(ToggleExpanded);
		_objective.OnMouseDown(e =>
		{
			if (e.Button == MouseButton.Left)
			{
				e.Handled = true;
				ToggleExpanded();
			}
		});
		_edit = ActionButton("pencil", Strings.GoalEdit, BeginEdit);
		_pause = ActionButton("pause", Strings.GoalPause, pause);
		_resume = ActionButton("play", Strings.GoalResume, resume);
		// 轮次上限阻塞时恢复会立刻再撞上限：把「再加 N 轮」做成可见按钮，别只藏在 tooltip 里。
		_extend = new Button().Ghost()
			.Content(new TextBlock().Text($"+{GoalMode.ExtraRounds} 轮")
				.FontSize(AppTheme.FontSizeMicro)
				.PrimaryText())
			.HoverTip(Strings.GoalExtendRounds)
			.Height(AppTheme.ControlHeightSm)
			.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
			.VerticalAlignment(VerticalAlignment.Center)
			.OnClick(resume);
		_clear = ActionButton("trash", Strings.GoalClear, clear);

		var actions = new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space1)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_edit, _pause, _resume, _extend, _clear);

		_browseRow = new Grid()
			.Columns("Auto,Auto,*,Auto,Auto")
			.MinHeight(AppTheme.ControlHeightLg)
			.Children(
				_goalIcon.Column(0),
				_phase.Column(1),
				_objective.Column(2),
				expand.Column(3),
				actions.Column(4));

		// 编辑行与浏览行同格重叠，用可见性切换：不重建卡片，也不动外层布局。
		_editStatus.FontSize(AppTheme.FontSizeMicro)
			.SubtleText()
			.TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(AppTheme.Space2 + 2, 0, 0, 0));
		var editHeader = new Grid()
			.Columns("Auto,Auto,*,Auto")
			.MinHeight(AppTheme.ControlHeightSm)
			.Margin(new Thickness(0, AppTheme.Space2, 0, 0))
			.Children(
				AppIcons.Icon("target", AppStyles.RoleInfo, 13)
					.Margin(new Thickness(0, 0, AppTheme.Space2, 0))
					.VerticalAlignment(VerticalAlignment.Center).Column(0),
				new TextBlock().Text(Strings.GoalEditTitle)
					.FontSize(AppTheme.FontSizeCaption)
					.PrimaryText()
					.VerticalAlignment(VerticalAlignment.Center).Column(1),
				_editStatus.Column(2),
				new StackPanel()
					.Orientation(Orientation.Horizontal)
					.Spacing(2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						AppIcons.Button("check", Strings.GoalSaveEdit, () => CommitEdit(edit), size: 13)
							.Width(AppTheme.ControlHeightSm).Height(AppTheme.ControlHeightSm),
						AppIcons.Button("x", Strings.GoalCancelEdit, CancelEdit, size: 13)
							.Width(AppTheme.ControlHeightSm).Height(AppTheme.ControlHeightSm))
					.Column(3));
		_editRow = new StackPanel()
			.Spacing(2)
			.IsVisible(false)
			.Children(editHeader, _editEditorHost);

		View = new Border()
			.StyleName(AppStyles.Card)
			.CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3, 0, AppTheme.Space1, 0))
			.IsVisible(false)
			.Child(new Grid().Children(
				new StackPanel().Children(_browseRow, _details),
				_editRow));
	}

	public Border View { get; }

	internal bool IsShown => View.IsVisible;

	internal string PhaseTextForTests => _phase.Text;

	internal string ObjectiveTextForTests => _objective.Text;

	/// 工作台摘要只给状态：阶段、轮次与预算；目标全文留在目标条和 tooltip 里。
	internal string Summary => _phase.Text;

	internal bool IsExpanded => _expanded;

	internal void ToggleExpandedForTests() => ToggleExpanded();

	internal MultiLineTextBox EditorForTests => _editor
		?? throw new InvalidOperationException("目标编辑框只在编辑态创建。");

	internal void BeginEditForTests() => BeginEdit();

	internal void CommitEditForTests() => CommitEdit(_editAction);

	internal void InsertReferencesForTests(IReadOnlyList<string> paths) => InsertReferences(paths);

	/// 操作进行中：编辑、恢复、清除禁用，避免同一份状态连点两次。
	/// 暂停保持可用：恢复会跑完整轮，用户必须能中途叫停。
	internal void SetBusy(bool busy)
	{
		_edit.IsEnabled = !busy;
		_resume.IsEnabled = !busy;
		_extend.IsEnabled = !busy;
		_clear.IsEnabled = !busy;
	}

	/// 写入一份目标状态；与上一次相同时不动界面。
	/// 会话每次改目标都换一个新实例，引用相等就够，不必逐字段比较整段目标文本。
	/// <paramref name="usedTokens"/> 是目标开始后累计的 token，只有设了预算才显示。
	public void Render(GoalState goal, long usedTokens = 0)
	{
		// 同一份 GoalState 在整轮里不会换新实例，但 token 用量会涨：把用量折进比较键，
		// 否则「x/y tk」会停在本轮开始那一刻。100 token 一档，够用又不至于逐 token 重排。
		var usedBucket = goal.MaxTokens > 0 ? usedTokens / 100 : 0;
		if (ReferenceEquals(_rendered, goal) && _renderedUsedBucket == usedBucket)
		{
			return;
		}

		var previous = _rendered;
		_rendered = goal;
		_renderedUsedBucket = usedBucket;
		if (previous is not null && !string.Equals(previous.Objective, goal.Objective, StringComparison.Ordinal))
		{
			// 目标换了：回到单行摘要，别把旧目标的展开态带过来。
			_expanded = false;
			_details.IsVisible(false);
			_chevron.Kind(GlyphKind.ChevronDown);
		}
		// 完成态不占位（PiDeck 同义）：完成消息与 toast 已经在时间线上说过一次。
		var visible = goal.Objective.Length > 0 && goal.Phase != GoalState.Complete;
		View.IsVisible(visible);
		if (!visible)
		{
			// 目标被清除或完成：编辑草稿一起收掉，别在下个目标上复活。
			EndEdit();
			return;
		}

		// 目标被外部替换时收掉草稿：继续保存会把新目标覆盖成旧文本。
		if (_editing && previous is not null &&
			!string.Equals(previous.Objective, goal.Objective, StringComparison.Ordinal))
		{
			EndEdit();
		}

		var phase = Strings.GoalPhase(goal.Phase);
		var phaseRole = goal.Phase switch
		{
			GoalState.Active => AppStyles.RoleInfo,
			GoalState.Paused => AppStyles.RoleWarning,
			GoalState.Blocked => AppStyles.RoleError,
			_ => AppStyles.RolePrimary,
		};
		_phase.Role(phaseRole);
		_goalIcon.Source(AppIcons.Get("target", AppStyles.ColorForRole(phaseRole)));
		_phase.Text(Strings.GoalProgress(
			phase,
			goal.RoundsStarted,
			goal.MaxRounds,
			usedTokens,
			goal.MaxTokens));
		_editStatus.Text(_phase.Text);
		if (goal.Phase == GoalState.Blocked && goal.BlockReason is { Length: > 0 } reason)
		{
			_phase.HoverTip(reason);
		}
		else
		{
			_phase.HoverTip(null);
		}

		// 阻塞时目标位置显示原因：这是用户此刻最需要看到的一句话；目标全文进 tooltip。
		var blockedReason = goal.Phase == GoalState.Blocked && goal.BlockReason is { Length: > 0 }
			? goal.BlockReason
			: null;
		_objective.Text(blockedReason ?? goal.Objective);
		_objective.Role(blockedReason is null ? AppStyles.RoleMuted : AppStyles.RoleError);
		_objective.HoverTip(blockedReason is null
			? $"{Strings.GoalExpandHint}\n{goal.Objective}"
			: $"{Strings.GoalExpandHint}\n{goal.BlockReason}\n{goal.Objective}");
		_details.Text(blockedReason is null ? goal.Objective : $"{goal.BlockReason}\n{goal.Objective}");
		_details.IsVisible(_expanded);
		_pause.IsVisible(goal.Phase == GoalState.Active);
		// 阻塞也给恢复：只能清除的话，条上就没有纠错入口。
		var limitBlocked = goal.Phase == GoalState.Blocked && goal.BlockCode == GoalMode.RoundLimitCode;
		_resume.IsVisible(goal.Phase is GoalState.Paused or GoalState.Blocked && !limitBlocked);
		_extend.IsVisible(limitBlocked);
		_resume.HoverTip(goal.BlockCode == GoalMode.TokenLimitCode
			? Strings.GoalResetTokenBudget
			: Strings.GoalResume);
	}

	/// 目标编辑自己接管落点：文件拖到框里就插入 @ 引用，不再让事件冒泡去主输入框加附件。
	private void OnEditorDragOver(DragEventArgs e)
	{
		var accepts = _editing && _editor is not null && ComposerDrop.CanAccept(e.Data) &&
			(e.AllowedEffects & DragDropEffects.Copy) != 0;
		e.Accepted = accepts;
		e.Effect = accepts ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnEditorDrop(DragEventArgs e)
	{
		OnEditorDragOver(e);
		if (!e.Accepted)
		{
			return;
		}

		e.Accepted = false;
		e.Effect = DragDropEffects.None;
		var paths = ComposerDrop.Paths(e.Data, _cwd ?? Environment.CurrentDirectory);
		if (paths.Count == 0)
		{
			return;
		}

		InsertReferences(paths);
		e.Accepted = true;
		e.Effect = DragDropEffects.Copy;
		e.Handled = true;
	}

	private void InsertReferences(IReadOnlyList<string> paths)
	{
		if (_editor is not { } editor)
		{
			return;
		}

		var text = editor.Text;
		var start = Math.Clamp(editor.SelectionStart, 0, text.Length);
		var length = Math.Clamp(editor.SelectionLength, 0, text.Length - start);
		var end = start + length;
		var references = string.Join(" ", paths.Select(ComposerDrop.Reference));
		var before = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : string.Empty;
		var after = end < text.Length && !char.IsWhiteSpace(text[end]) ? " " : string.Empty;
		var insertion = before + references + after;
		editor.Text = string.Concat(text.AsSpan(0, start), insertion, text.AsSpan(end));
		editor.Select(start + insertion.Length, 0);
		editor.Focus();
		editor.ScrollToCaret();
	}

	private void BeginEdit()
	{
		if (_editing || _rendered is not { Objective.Length: > 0 } goal)
		{
			return;
		}

		var editor = CreateEditor();
		_editor = editor;
		_editEditorHost.Children(editor);
		editor.Text(goal.Objective);
		_editing = true;
		_details.IsVisible(false);
		_browseRow.IsVisible(false);
		_editStatus.Text(_phase.Text);
		_editRow.IsVisible(true);
		editor.Focus();
		editor.Select(editor.Text.Length, 0);
	}

	/// 多行输入沿用主输入框的约定：Enter 保存，Shift+Enter 换行；长目标在框内滚动。
	private MultiLineTextBox CreateEditor()
	{
		var editor = new MultiLineTextBox().PrimarySelection()
			.StyleName(AppStyles.TextInput)
			.FontSize(AppTheme.FontSizeCaption)
			.Height(64)
			// 编辑框在原卡片里画一圈输入边框：它和主输入框不是同一个输入面，需要一条边界。
			.BorderThickness(1)
			.Margin(new Thickness(0, 2, AppTheme.Space1, 2))
			.HoverTip(Strings.GoalEditHint);
		// 与主输入框同一套 @ 引用：原始文本仍是完整路径，只是显示成行内 chip。
		UserReferenceRichText.Attach(editor, editable: true, cwd: _cwd);
		editor.OnKeyDown(key =>
		{
			if (UserReferenceRichText.TryHandleKey(editor, key))
			{
				return;
			}

			if (key.Key == Key.Enter && !key.ShiftKey)
			{
				key.Handled = true;
				CommitEdit(_editAction);
			}
			else if (key.Key == Key.Escape)
			{
				key.Handled = true;
				CancelEdit();
			}
		});
		editor.OnTextInput(input => UserReferenceRichText.TryHandleTextInput(editor, input));
		editor.AllowDrop()
			.OnDragEnter(OnEditorDragOver)
			.OnDragOver(OnEditorDragOver)
			.OnDrop(OnEditorDrop);
		return editor;
	}

	private void CommitEdit(Action<string> edit)
	{
		if (!_editing || _editor is not { } editor)
		{
			return;
		}

		var text = editor.Text.Trim();
		EndEdit();
		if (text.Length > 0)
		{
			edit(text);
		}
	}

	private void CancelEdit() => EndEdit();

	private void EndEdit()
	{
		if (_editing)
		{
			// 编辑行收起后 TextBox 还在树里但不可见：不主动移焦，按键会继续落进去。
			ReleaseEditorFocus();
		}

		_editing = false;
		if (_editor is { } editor)
		{
			_editEditorHost.Remove(editor);
			_editor = null;
		}

		_editRow.IsVisible(false);
		_browseRow.IsVisible(true);
		_details.IsVisible(_expanded);
	}

	private void ToggleExpanded()
	{
		_expanded = !_expanded;
		_details.IsVisible(_expanded);
		_chevron.Kind(_expanded ? GlyphKind.ChevronUp : GlyphKind.ChevronDown);
	}

	private void ReleaseEditorFocus()
	{
		if (_editor is not { } editor || View.FindVisualRoot() is not Window window)
		{
			return;
		}

		if (ReferenceEquals(window.FocusManager.FocusedElement, editor))
		{
			window.FocusManager.ClearFocus();
		}
	}

	private static Button ActionButton(string icon, string tip, Action onClick) =>
		AppIcons.Button(icon, tip, onClick, size: 14)
			.Width(AppTheme.ControlHeightSm)
			.Height(AppTheme.ControlHeightSm)
			.CornerRadius(AppTheme.ControlHeightSm / 2);
}
