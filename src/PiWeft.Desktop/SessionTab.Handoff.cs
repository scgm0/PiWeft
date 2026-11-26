using Aprillz.MewUI;
using PiWeft.Cli;
using PiWeft.CodingAgent.Subagents;

namespace PiWeft.Desktop;

/// 交接简报的会话侧：收集目标、计划、待办、工作区与最近对话，交给 <see cref="HandoffBrief"/> 排版。
/// 简报只读当前状态，不写会话、不起模型；复制是唯一动作。
public sealed partial class SessionTab
{
	private const int HandoffSubagentLimit = 8;

	/// 收集并生成一份交接简报（读取 git status 会起子进程，调用方应放到线程池）。
	internal string BuildHandoffBrief()
	{
		var input = CaptureHandoffInput();
		var status = GitStatusReader.Read(Session.Cwd, TimeSpan.FromSeconds(5));
		return HandoffBrief.Build(WithGit(input, status));
	}

	/// 会话侧快照在 UI 线程取：计划、待办、子代理与最近对话都读活会话，
	/// 放后台会读到混着两个时刻的状态，也会与 UI 线程争会话锁。
	private HandoffBriefInput CaptureHandoffInput()
	{
		var runs = Session.Subagents?.Runs ?? [];
		var subagents = runs.Count <= HandoffSubagentLimit
			? runs
			: [.. runs.Skip(runs.Count - HandoffSubagentLimit)];
		var entries = _chat.Entries();
		var (userMessages, lastAssistant) = RecentConversation(entries);
		return new HandoffBriefInput(
			Title: Title,
			Cwd: Session.Cwd,
			Model: $"{Session.Model.Provider}/{Session.Model.Id}",
			Thinking: Session.ThinkingLevel,
			SessionFile: Session.Session.SessionFile ?? string.Empty,
			Goal: Session.Goal,
			PlanSteps: Session.ActivePlanSteps,
			PlanExecutionEnabled: Session.PlanExecutionEnabled,
			Todos: Session.CurrentTodo().ActivePlan?.Items ?? [],
			Branch: string.Empty,
			Ahead: 0,
			Behind: 0,
			GitError: null,
			Uncommitted: [],
			TurnChanges: LastTurnChanges(entries),
			Subagents: subagents,
			RecentUserMessages: userMessages,
			LastAssistant: lastAssistant,
			Now: DateTimeOffset.Now);
	}

	private static HandoffBriefInput WithGit(HandoffBriefInput input, GitStatus status) => input with
	{
		Branch = status.Branch,
		Ahead = status.Ahead,
		Behind = status.Behind,
		GitError = status.IsRepository ? null : status.Error ?? Strings.GitNotRepository,
		Uncommitted = status.Changes,
	};

	/// 复制到剪贴板并提示字数；生成失败只报一句，不把异常文本塞进剪贴板。
	internal async Task CopyHandoffBriefAsync()
	{
		if (_disposed)
		{
			return;
		}

		// UI 线程先取会话快照，线程池里只跑 git status 与排版。
		var input = CaptureHandoffInput();
		string brief;
		try
		{
			brief = await Task.Run(() =>
			{
				var status = GitStatusReader.Read(Session.Cwd, TimeSpan.FromSeconds(5));
				return HandoffBrief.Build(WithGit(input, status));
			}).ConfigureAwait(true);
		}
		catch (Exception ex)
		{
			AppLog.Failure(AppLog.For<SessionTab>(), "Build handoff brief", ex);
			if (!_disposed)
			{
				NotifyError(Strings.HandoffFailed(ex.Message));
			}

			return;
		}

		if (_disposed)
		{
			// 生成期间标签被关掉：视图已摘，不再弹提示。
			return;
		}

		var clipboard = Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;
		if (clipboard?.TrySetText(brief) == true)
		{
			Notify(Strings.HandoffCopied(brief.Length));
		}
		else
		{
			NotifyError(Strings.ClipboardUnavailable);
		}
	}

	/// 往输入框追加一段：已有草稿就另起一段，不覆盖用户正在写的内容。
	internal void AppendToComposer(string text)
	{
		if (_disposed)
		{
			return;
		}

		var existing = _composer.Text.TrimEnd();
		Prefill(existing.Length == 0 ? text : existing + "\n\n" + text);
		Notify(Strings.DiffQuotedToInput);
	}

	/// 把审查提示词放进输入框：已有草稿就追加，不覆盖用户正在写的内容。
	/// 只填不发送——审查是否开始由用户按发送决定。
	internal void InsertChangeReviewPrompt()
	{
		if (_disposed)
		{
			return;
		}

		var existing = _composer.Text.TrimEnd();
		Prefill(existing.Length == 0
			? Strings.ChangeReviewPrompt
			: existing + "\n\n" + Strings.ChangeReviewPrompt);
		Notify(Strings.ChangeReviewPromptInserted);
	}

	/// 最后一轮改过的文件：与侧栏「N 文件」同一套取法，只留最近一条非空记录。
	private static IReadOnlyList<ChatFileChange> LastTurnChanges(IReadOnlyList<ChatEntry> entries)
	{
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			if (entries[index].ChangedFiles.Count > 0)
			{
				return entries[index].ChangedFiles;
			}
		}

		return [];
	}

	/// 最近三条用户消息与最后一条助手正文。倒着扫一次，不复制整段历史。
	private static (IReadOnlyList<string> Users, string Assistant) RecentConversation(IReadOnlyList<ChatEntry> entries)
	{
		var users = new List<string>(3);
		var assistant = string.Empty;
		for (var index = entries.Count - 1; index >= 0; index--)
		{
			var entry = entries[index];
			// 先看长度再物化：大工具结果的正文在会话文件里，不该为了判空读出来。
			if (entry.TextLength == 0)
			{
				continue;
			}

			var text = entry.Text;

			if (entry.Role == ChatRole.Assistant && assistant.Length == 0)
			{
				assistant = text;
			}
			// 带 Label 的用户行是注入内容（子代理回报、关联会话消息、继续标记），
			// 不是用户自己说的话；简报里写成“用户：”会误导接手的人。
			else if (entry.Role == ChatRole.User && entry.Label is null && users.Count < 3)
			{
				users.Add(text);
			}

			if (users.Count >= 3 && assistant.Length > 0)
			{
				break;
			}
		}

		users.Reverse();
		return (users, assistant);
	}
}
