using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 输入区的「+」菜单、模式 chip、技能/提示词选择器与快捷消息浮层。合并入口的行为都放这里，
/// 主文件只保留 composer 的布局与发送链路。
public sealed partial class SessionTab
{
	/// 当前工作模式。目标来自输入区开关，计划落在会话状态上：计划开始执行时
	/// 会话会把 <see cref="PiWeft.CodingAgent.CodingAgentSession.PlanModeEnabled"/> 置回 false，
	/// chip 跟着隐藏，不额外维护第二份计划状态。
	internal ComposerMode Mode => Session.PlanModeEnabled ? ComposerMode.Plan : _goalMode ? ComposerMode.Goal : ComposerMode.Normal;

	internal ComposerAddMenu ComposerAddMenuForTests => _composerAddMenu;

	internal ComposerPicker ComposerPickerForTests => _composerPicker;

	/// 模式 chip 的可见文案；隐藏时返回 null。
	internal string? ModeChipTextForTests => _modeChip.IsVisible ? _modeChipLabel.Text : null;

	internal void SelectModeForTests(ComposerMode mode) => SelectMode(mode);

	/// 仅供 --ui-smoke composer-add：打开合并菜单，外部脚本趁机截图。
	internal void PrepareComposerAddSmoke() => ShowComposerAddMenu();

	/// 仅供 --ui-smoke composer-picker：打开技能选择器，外部脚本趁机截图。
	internal void PrepareSkillPickerSmoke() => ShowSkillPicker();

	/// 仅供 --ui-smoke quick-messages：打开快捷消息浮层，外部脚本趁机截图。
	internal void PrepareQuickMessagesSmoke() => ShowQuickMessages();

	/// 快捷消息浮层：每次打开重读一份清单，用户在设置里改完切回来就能看到。
	internal void ShowQuickMessages()
	{
		if (_disposed)
		{
			return;
		}

		// 再按一次快捷键收起：呼出类快捷键的普遍预期。
		if (_quickMessages.IsOpen)
		{
			_quickMessages.Hide();
			return;
		}

		HoverHints.Cancel(_quickMessagesButton);
		_quickMessages.Show(_quickMessagesButton, DesktopPreference.Load().QuickMessageList, CanSendQuickMessage(),
			InsertQuickMessage, SendQuickMessage, () => QuickMessagesSettingsRequested?.Invoke(this), FocusComposer);
	}

	private void InsertQuickMessage(string text)
	{
		if (_disposed)
		{
			return;
		}

		_composer.Text = QuickMessages.Append(_composer.Text, text);
		_composer.Select(_composer.Text.Length, 0);
		_composer.Focus();
	}

	/// 直发不经过草稿：用清单原文发一条，用户写了一半的输入与附件都保留（PiDeck 同一契约）。
	private void SendQuickMessage(string text)
	{
		if (_disposed || !CanSendQuickMessage())
		{
			return;
		}

		var message = text.Trim();
		if (message.Length == 0)
		{
			return;
		}

		_pendingSend = SendAsync(message);
	}

	/// 直发可用性：运行中发送键是中断，附件与编辑重发状态也不该被清单消息带走。
	private bool CanSendQuickMessage() =>
		!_sending && !_chat.IsStreaming && _loadingAttachments == 0 &&
		_attachments.Count == 0 && _pendingBranchEntryId is null;

	private void ShowComposerAddMenu()
	{
		if (_disposed)
		{
			return;
		}

		HoverHints.Cancel(_addButton);
		_composerAddMenu.Show(_addButton, new ComposerAddMenu.Options(
			Mode,
			() => _ = AttachImageAsync(),
			ShowSkillPicker,
			ShowPromptPicker,
			SelectMode,
			// 流式期间工具集不能换；计划执行中换模式会丢掉待办进度。
			CanSelectMode: !IsStreaming && !Session.PlanExecutionEnabled));
	}

	private void ShowSkillPicker()
	{
		if (_disposed)
		{
			return;
		}

		_composerPicker.Show(View, Strings.SkillPickerTitle, Strings.SkillPickerPlaceholder,
			Strings.SkillPickerEmpty, Strings.SkillPickerNoMatch,
			[.. Session.DiscoveredSkills.Select(static skill => new ComposerPicker.Item(
				"sparkles", $"skill:{skill.Name}", skill.Name, skill.Description, skill.Origin.Display))],
			InsertPickerCommand);
	}

	private void ShowPromptPicker()
	{
		if (_disposed)
		{
			return;
		}

		_composerPicker.Show(View, Strings.PromptPickerTitle, Strings.PromptPickerPlaceholder,
			Strings.PromptPickerEmpty, Strings.PromptPickerNoMatch,
			[.. Session.PromptTemplates.Select(static template => new ComposerPicker.Item(
				"file-text", template.Name, template.Name, template.Description ?? string.Empty, template.Origin.Display))],
			InsertPickerCommand);
	}

	/// 选中条目只插入命令，不直接发送：用户还要补参数或改别的内容，回车由自己按。
	private void InsertPickerCommand(ComposerPicker.Item item)
	{
		if (_disposed)
		{
			return;
		}

		_composer.Text = ComposerPicker.AppendCommand(_composer.Text, item.Token);
		_composer.Select(_composer.Text.Length, 0);
		_composer.Focus();
	}

	/// 目标与计划互斥；再选一次当前项回普通。退出目标时立即暂停，
	/// 不等下一条无标记消息（PiDeck 的 pi 分支同样马上发 /goal pause）。
	private void SelectMode(ComposerMode mode)
	{
		// 计划执行中不能改模式：进入或退出计划模式都会清掉正在勾进度的步骤
		// （SetPlanMode 同时结束执行态），用户会丢掉整张待办。执行完或停止后再切换。
		if (_disposed || IsStreaming || Session.PlanExecutionEnabled)
		{
			return;
		}

		if (mode == Mode)
		{
			mode = ComposerMode.Normal;
		}

		_goalMode = mode == ComposerMode.Goal;
		Session.SetPlanMode(mode == ComposerMode.Plan);
		if (!_goalMode && Session.Goal.Running)
		{
			Session.PauseGoal();
		}

		_renderedMode = mode;
		UpdateModeChip();
		Render();
	}

	private void UpdateModeChip()
	{
		var mode = Mode;
		_goalModeIcon.IsVisible(mode == ComposerMode.Goal);
		_planModeIcon.IsVisible(mode == ComposerMode.Plan);
		_modeChip.IsVisible(mode != ComposerMode.Normal);
		if (mode == ComposerMode.Normal)
		{
			return;
		}

		_modeChipLabel.Text(mode == ComposerMode.Goal ? Strings.ModeGoal : Strings.ModePlan);
		_modeChip.HoverTip(mode == ComposerMode.Goal ? Strings.ModeGoalExit : Strings.ModePlanExit);
	}
}
