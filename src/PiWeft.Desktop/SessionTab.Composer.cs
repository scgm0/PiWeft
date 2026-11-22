using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Subagents;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// 输入区的「+」菜单、模式 chip、技能/提示词选择器与快捷消息浮层。合并入口的行为都放这里，
/// 主文件只保留 composer 的布局与发送链路。
public sealed partial class SessionTab
{
	/// 恢复目标的请求只发一次：续跑会直接拉起一轮模型，连点会排两轮。
	private bool _goalResuming;

	/// 当前工作模式。目标来自输入区开关，计划落在会话状态上：计划开始执行时
	/// 会话会把 <see cref="PiWeft.CodingAgent.CodingAgentSession.PlanModeEnabled"/> 置回 false，
	/// chip 跟着隐藏，不额外维护第二份计划状态。
	/// 目标条恢复出来的目标（`_goalMode` 为 false）也要把 chip 切成目标：
	/// 状态条在跑而 chip 说普通，读者会以为模式没生效。
	internal ComposerMode Mode
	{
		get
		{
			if (Session.PlanModeEnabled)
			{
				return ComposerMode.Plan;
			}

			// 每帧都会读一次：只取一份 Goal 快照，别读两遍属性。
			var goal = Session.Goal;
			return _goalMode || goal.Running ? ComposerMode.Goal : ComposerMode.Normal;
		}
	}

	internal ComposerAddMenu ComposerAddMenuForTests => _composerAddMenu;

	internal Button ModeChipForTests => _modeChip;

	internal Button ModelButtonForTests => _modelButton;

	internal ComposerPicker ComposerPickerForTests => _composerPicker;

	/// 模式 chip 的可见文案；隐藏时返回 null。
	internal string? ModeChipTextForTests => _modeChip.IsVisible ? _modeChipLabel.Text : null;

	internal void SelectModeForTests(ComposerMode mode) => SelectMode(mode);

	internal ComposerMode? PendingModeForTests => _pendingMode;

	/// 模拟流式期间点下的模式：真实路径由 <see cref="SelectMode"/> 在 IsStreaming 时写入。
	internal void QueueModeForTests(ComposerMode mode)
	{
		_pendingMode = mode;
		UpdateModeChip();
	}

	internal void ApplyPendingModeForTests()
	{
		if (_pendingMode is { } mode)
		{
			_pendingMode = null;
			ApplyMode(mode);
		}
	}

	internal bool GoalStripVisible => _goalStrip.IsShown;

	internal string? GoalStripPhaseForTests => _goalStrip.IsShown ? _goalStrip.PhaseTextForTests : null;

	internal string? GoalStripObjectiveForTests => _goalStrip.IsShown ? _goalStrip.ObjectiveTextForTests : null;

	internal bool LinkedCardVisible => _linkedSessionsCard.IsShown;

	internal int LinkedCardRowCount => _linkedSessionsCard.RowCount;

	internal bool SubagentCardVisible => _subagentCard.IsShown;

	internal int SubagentCardRowCount => _subagentCard.RowCount;

	internal SubagentCard SubagentCardForTests => _subagentCard;

	private void OnLinkedSessionsChanged() => Dispatch(RefreshLinkedSessions);

	private int _subagentRefreshQueued;

	// 子代理每开始一个工具都会触发 Changed。逐条 BeginInvoke 会把整张卡重建多次，
	// 顺带打掉正在显示的 tooltip；同一拍里只排一次刷新。
	private void OnSubagentsChanged()
	{
		if (Interlocked.Exchange(ref _subagentRefreshQueued, 1) == 1)
		{
			return;
		}

		Dispatch(() =>
		{
			Interlocked.Exchange(ref _subagentRefreshQueued, 0);
			RefreshSubagents();
		});
	}

	/// 子代理条：启动、结束、停止都会触发；重建行与卡片可见性。
	internal void RefreshSubagents()
	{
		if (_disposed)
		{
			return;
		}

		_subagentCard.Render(Session.Subagents);
		RefreshWidgetColumn();

		// 页签圆点只在「有子代理开始/结束等回答」时翻转；工具活动不碰标题栏。
		// 用卡片这一帧的快照，不再让 hub 扫描第二遍。
		var awaiting = _subagentCard.HasAwaiting;
		if (awaiting != _awaitingSubagents)
		{
			_awaitingSubagents = awaiting;
			SubagentAwaitChanged?.Invoke(this);
		}
	}

	private bool StopSubagent(string name)
	{
		if (_disposed || Session.Subagents is not { } hub)
		{
			return false;
		}

		try
		{
			return hub.Stop(name).Ok;
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Stop subagent", ex);
			return false;
		}
	}

	private void SubagentFailed(string message)
	{
		if (!_disposed)
		{
			NotifyError(message);
		}
	}

	/// 子会话条：hub 变化（提议/子会话状态/回报）或首次挂载时重建。
	internal void RefreshLinkedSessions()
	{
		if (_disposed)
		{
			return;
		}

		_linkedSessionsCard.Render(Session.Linked, OpenLinkedSession, LinkedActionFailed);
		RefreshWidgetColumn();
	}

	private void OpenLinkedSession(string sessionId) => LinkedSessionOpenRequested?.Invoke(sessionId);

	private void LinkedActionFailed(string message) => Dispatch(() =>
	{
		if (!_disposed)
		{
			NotifyError(Strings.LinkedProposalFailed(message));
		}
	});

	/// 目标条与扩展卡分开：widget 推送与每帧渲染都会调这里，状态没变就不碰界面。
	internal void RefreshGoalStrip()
	{
		if (_disposed)
		{
			return;
		}

		var goal = Session.Goal;
		_renderedGoalState = goal;
		// 目标设置预算时才读统计：没有预算的常见路径不必碰统计缓存。
		var usedTokens = goal.MaxTokens > 0
			? Math.Max(0, Session.Statistics().Tokens - goal.TokensAtStart)
			: 0;
		_goalStrip.Render(goal, usedTokens);
		RefreshWidgetColumn();
		RefreshModeMarker();
	}

	private void RefreshModeMarker()
	{
		var running = HasRunningMode;
		if (running == _renderedModeRunning)
		{
			return;
		}

		_renderedModeRunning = running;
		ModeChanged?.Invoke(this);
	}

	private void PauseGoal()
	{
		if (_disposed || !Session.Goal.Running)
		{
			return;
		}

		try
		{
			_goalStrip.SetBusy(true);
			Session.PauseGoal(abortRun: true);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Pause goal", ex);
			NotifyError(Strings.GoalActionFailed(ex.Message));
		}
		finally
		{
			if (!_disposed)
			{
				_goalStrip.SetBusy(false);
				RefreshGoalStrip();
			}
		}
	}

	private void ResumeGoal()
	{
		if (_disposed || _goalResuming)
		{
			return;
		}

		_ = ResumeGoalAsync();
	}

	private void EditGoal(string objective)
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_goalStrip.SetBusy(true);
			Session.EditGoal(objective);
			// 编辑结束把焦点还给输入框：编辑行收起后焦点已清，读者下一步多半是继续写。
			FocusComposer();
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Edit goal", ex);
			NotifyError(Strings.GoalActionFailed(ex.Message));
		}
		finally
		{
			if (!_disposed)
			{
				_goalStrip.SetBusy(false);
				RefreshGoalStrip();
			}
		}
	}

	private async Task ResumeGoalAsync()
	{
		_goalResuming = true;
		_goalStrip.SetBusy(true);
		try
		{
			await Session.ResumeGoalAsync();
		}
		catch (OperationCanceledException)
		{
			// 用户中断：与发送路径一致，不当作错误。
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Resume goal", ex);
			NotifyError(Strings.GoalResumeFailed(ex.Message));
		}
		finally
		{
			_goalResuming = false;
			if (!_disposed)
			{
				_goalStrip.SetBusy(false);
				RefreshGoalStrip();
			}
		}
	}

	private async void ConfirmClearGoal()
	{
		if (_disposed || Session.Goal.Objective.Length == 0)
		{
			return;
		}

		try
		{
			if (!await MessageBox.ConfirmAsync(Strings.GoalClearConfirmTitle, detail: Strings.GoalClearConfirmMessage) || _disposed)
			{
				return;
			}

			try
			{
				_goalStrip.SetBusy(true);
				Session.ClearGoal();
			}
			catch (Exception ex)
			{
				AppLog.Failure(AppLog.For<SessionTab>(), "Clear goal", ex);
				NotifyError(Strings.GoalActionFailed(ex.Message));
			}
			finally
			{
				_goalStrip.SetBusy(false);
				if (!_disposed)
				{
					RefreshGoalStrip();
				}
			}
		}
		catch (Exception ex)
		{
			// 确认弹窗本身失败：不再往下走，只记一条。
			AppLog.Failure(AppLog.For<SessionTab>(), "Confirm clear goal", ex);
		}
	}

	/// 仅供 --ui-smoke composer-add：打开合并菜单，外部脚本趁机截图。
	internal void PrepareComposerAddSmoke() => ShowComposerAddMenu();

	/// 仅供 --ui-smoke composer-picker：打开技能选择器，外部脚本趁机截图。
	internal void PrepareSkillPickerSmoke() => ShowSkillPicker();

	/// 仅供 --ui-smoke linked-sessions：登记一张 own_initiative 提议，外部脚本截图子会话条。
	/// 只进内存，不写会话文件，也不开子会话。
	internal void PrepareLinkedSessionsSmoke() =>
		_ = Session.Linked?.StartAsync(
			"补桌面测试",
			"给子会话条补一组布局截图，检查提议卡片在窄窗口下的换行。",
			LinkedSessionProposal.OwnInitiative);

	/// 仅供 --ui-smoke subagents：把子代理卡摆成运行中 / 失败可继续 / 等待回答三行，外部脚本截图。
	/// 只进内存，不建子会话，不写会话文件。
	internal void PrepareSubagentsSmoke()
	{
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		SubagentRun[] runs =
		[
			new()
			{
				Name = "desktop-hot",
				SessionId = "subagent-smoke-running",
				Agent = "scout",
				Label = "桌面热路径与每帧分配审计",
				Brief = "在仓库里找出每帧都会走到的路径，列出分配热点。",
				Model = "opencode-go/deepseek-v4.1-flash",
				Thinking = "max",
				State = SubagentState.Running,
				Turns = 1,
				Activity = "read src/PiWeft.Desktop/SessionTab.cs",
				ToolCalls = 7,
				LastActivityAt = now - 3_000,
				Tokens = 31_500,
				Cost = 0.0071,
				StartedAt = now - 42_000,
			},
			new()
			{
				Name = "core-hot",
				SessionId = "subagent-smoke-failed",
				SessionFile = "/tmp/piweft-subagents/core-hot.jsonl",
				Agent = "scout",
				Label = "核心库热路径与并发审计",
				Brief = "审计核心库的并发路径与锁粒度。",
				Model = "opencode-go/deepseek-v4.1-flash",
				Thinking = "max",
				State = SubagentState.Failed,
				Turns = 1,
				Result = "Request aborted",
				Tokens = 102_932,
				Cost = 0.0257,
				StartedAt = now - 205_000,
				FinishedAt = now - 1_000,
				Restored = true,
			},
			new()
			{
				Name = "reviewer-1",
				SessionId = "subagent-smoke-awaiting",
				Agent = "reviewer",
				Label = "审查子代理卡的布局改动",
				Brief = "审查改动，给出 P0/P1 问题。",
				Model = "opencode-go/deepseek-v4.1-flash",
				Thinking = "high",
				State = SubagentState.Running,
				Turns = 1,
				AwaitingUser = true,
				Tokens = 18_240,
				Cost = 0.0042,
				StartedAt = now - 12_000,
			},
			new()
			{
				Name = "worker-1",
				SessionId = "subagent-smoke-verified",
				SessionFile = "/tmp/piweft-subagents/worker-1.jsonl",
				Agent = "worker",
				Label = "修掉会话卡顿并跟验证",
				Brief = "把每帧重建的列表改成增量更新，跑桌面测试。",
				Model = "opencode-go/deepseek-v4.1-flash",
				Thinking = "medium",
				State = SubagentState.Idle,
				Turns = 2,
				Result = "改成增量更新，桌面测试全过。",
				StructuredOutput = new JsonObject
				{
					["fixed"] = true,
					["tests"] = "passed",
				},
				Tokens = 41_800,
				MaxTokens = 120_000,
				ToolCalls = 12,
				Cost = 0.0093,
				StartedAt = now - 240_000,
				FinishedAt = now - 5_000,
				Verify =
				[
					new() { Command = "dotnet test tests/PiWeft.Desktop.Tests", ExitCode = 0, Output = "Passed! - Failed: 0, Passed: 214" },
				],
			},
		];

		_subagentCard.RenderRunsForSmoke(runs);
		RefreshWidgetColumn();
	}

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
		_attachments.Count == 0 && _pendingBranchEntryId is null && _pendingAssistantEditEntryId is null;

	private void ShowComposerAddMenu()
	{
		if (_disposed)
		{
			return;
		}

		HoverHints.Cancel(_addButton);
		_composerAddMenu.Show(_addButton, new ComposerAddMenu.Options(
			// 待切换优先：菜单勾要落在用户点过的那一项上，不能等本轮结束才回跳。
			_pendingMode ?? Mode,
			() => _ = AttachImageAsync(),
			ShowSkillPicker,
			ShowPromptPicker,
			SelectMode,
			// 流式期间也能点：切换排队到本轮收尾，不再把入口禁用。
			CanSelectMode: true));
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
	/// 流式期间不直接换工具集：记下目标模式，本轮收尾后再应用。
	private void SelectMode(ComposerMode mode)
	{
		if (_disposed)
		{
			return;
		}

		if (mode == Mode)
		{
			mode = ComposerMode.Normal;
		}

		if (IsStreaming)
		{
			// 点 chip 取消待切换：真实模式还是普通，pending 只是排队，不能反过来变成「退出普通」。
			if (mode == ComposerMode.Normal && _pendingMode is not null && Mode == ComposerMode.Normal)
			{
				_pendingMode = null;
				Notify(Strings.ModeQueueCancelled);
				UpdateModeChip();
				Render();
				return;
			}

			// 再点同一个待切换项 = 取消。
			_pendingMode = _pendingMode == mode ? null : mode;
			if (_pendingMode is { } pending)
			{
				Notify(Strings.ModeQueued(pending));
			}
			else
			{
				Notify(Strings.ModeQueueCancelled);
			}

			UpdateModeChip();
			Render();
			return;
		}

		_pendingMode = null;
		ApplyMode(mode);
	}

	/// 真正落模式。流式结束后的待切换也走这里，暂停目标、收执行态只有一份实现。
	private void ApplyMode(ComposerMode mode)
	{
		if (Session.PlanExecutionEnabled)
		{
			// 进入计划模式会重开草案，步骤由 SetPlanMode 清空；切到别的模式保留进度。
			Session.StopPlanExecution(keepSteps: mode != ComposerMode.Plan);
		}

		_goalMode = mode == ComposerMode.Goal;
		Session.SetPlanMode(mode == ComposerMode.Plan);
		if (!_goalMode && Session.Goal.Running)
		{
			// 与模式 chip 同步：目标被暂停必须有可见交代，否则目标条只是悄悄换了阶段名。
			Session.PauseGoal(notify: true);
		}

		_renderedMode = mode;
		UpdateModeChip();
		Render();
	}

	private void UpdateModeChip()
	{
		var actual = Mode;
		var target = _pendingMode ?? actual;
		// 待退出（pending 为普通）时 chip 仍显示当前模式，只是后面挂一句「本轮后退出」。
		var iconMode = target == ComposerMode.Normal ? actual : target;
		_goalModeIcon.IsVisible(iconMode == ComposerMode.Goal);
		_planModeIcon.IsVisible(iconMode == ComposerMode.Plan);
		_modeChip.IsVisible(actual != ComposerMode.Normal || _pendingMode is not null);
		// 占位提示是当前模式最轻的说明：用户不必先开「+」菜单才知道这一句会被当成什么。
		_composerHint.Text(Strings.ComposerPlaceholderFor(target));
		if (actual == ComposerMode.Normal && _pendingMode is null)
		{
			return;
		}

		if (_pendingMode is { } pending)
		{
			var currentLabel = actual == ComposerMode.Goal ? Strings.ModeGoal : Strings.ModePlan;
			var targetLabel = pending == ComposerMode.Goal ? Strings.ModeGoal : Strings.ModePlan;
			_modeChipLabel.Text(pending == ComposerMode.Normal
				? $"{currentLabel}{Strings.ModePendingExitSuffix}"
				: $"{targetLabel}{Strings.ModePendingSuffix}");
			_modeChip.HoverTip(Strings.ModeChipPendingTip);
			return;
		}

		_modeChipLabel.Text(actual == ComposerMode.Goal ? Strings.ModeGoal : Strings.ModePlan);
		_modeChip.HoverTip(actual == ComposerMode.Goal ? Strings.ModeGoalExit : Strings.ModePlanExit);
	}
}
