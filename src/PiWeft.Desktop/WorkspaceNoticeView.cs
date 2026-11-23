using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 跨会话占用的状态类别；卡头只报各类数量，不拿第一条标题当摘要。
internal enum WorkspaceNoticeKind
{
	Running,
	Changed,
	SameFile,
}

/// 同一项目里其他会话的活动条目：只存结构化状态，文本到展开列表时才拼。
/// 侧栏刷新稳定时 Set 命中逐项比较，不再为每条候选生成一句中文。
/// 值类型：每次侧栏刷新命中几条就 new 几个对象，条目只装几个引用。
internal readonly record struct WorkspaceNoticeEntry(
	string SessionId,
	WorkspaceNoticeKind Kind,
	string Title,
	int ChangedFiles = 0,
	string? FileName = null,
	bool Subagent = false)
{
	internal string Text => Kind switch
	{
		WorkspaceNoticeKind.Running when Subagent => Strings.WorkspaceNoticeSubagentRunning(Title),
		WorkspaceNoticeKind.Running => Strings.WorkspaceNoticeRunning(Title),
		WorkspaceNoticeKind.Changed when Subagent => Strings.WorkspaceNoticeSubagentChanged(Title),
		WorkspaceNoticeKind.Changed => Strings.WorkspaceNoticeChanged(Title, ChangedFiles),
		_ => Strings.WorkspaceNoticeSameFile(Title, FileName ?? string.Empty),
	};
}

/// 侧栏生成的一条占用：状态类别 + 标题与计数，不提前拼文本。
internal readonly record struct WorkspaceUsage(
	WorkspaceNoticeKind Kind,
	string Title,
	int ChangedFiles = 0,
	string? FileName = null,
	bool Subagent = false);

/// 跨会话占用提示：收进工作台的一节，卡头按状态给数量，展开后列出所有正在跑或改过文件的会话。
/// 不再是输入框上方单独一行；默认收起，和待办、子代理等扩展卡一致。
internal sealed class WorkspaceNoticeView
{
	private readonly ComposerWidgetCard _card;
	private readonly StackPanel _rows = new();
	private readonly List<WorkspaceNoticeEntry> _entries = [];
	private Action<string>? _open;

	public WorkspaceNoticeView()
	{
		_card = new ComposerWidgetCard("activity", Strings.WorkspaceNoticeTitle);
		_card.Body.Children(_rows);
		View = _card.View;
	}

	public Border View { get; }

	internal bool IsShown => _card.View.IsVisible;

	internal string Meta => _card.Meta;

	internal int Count => _entries.Count;

	internal bool IsExpanded => _card.IsExpanded;

	internal void Expand() => _card.IsExpanded = true;

	internal void ExpandForTests() => Expand();

	internal void ShowHeader(bool visible) => _card.ShowHeader(visible);

	/// 每次侧栏刷新都调用；条目没变只换跳转目标，不重建行。返回是否有可见变化。
	public bool Set(IReadOnlyList<WorkspaceNoticeEntry> entries, Action<string> open)
	{
		_open = open;
		if (SameEntries(entries))
		{
			return false;
		}

		_entries.Clear();
		_entries.AddRange(entries);
		_rows.Clear();
		foreach (var entry in _entries)
		{
			var openButton = AppIcons.TextButton("arrow-right", Strings.WorkspaceNoticeOpen,
				() => Open(entry.SessionId));
			var line = new DockPanel().LastChildFill().Margin(new Thickness(0, 2, 0, 2));
			line.Children(openButton.DockRight());
			line.Children(new TextBlock()
				.Text(entry.Text)
				.FontSize(AppTheme.FontSizeCaption)
				.SubtleText()
				.TextWrapping(TextWrapping.Wrap));
			_rows.Children(line);
		}

		var running = 0;
		var changed = 0;
		var sameFile = 0;
		foreach (var entry in _entries)
		{
			switch (entry.Kind)
			{
				case WorkspaceNoticeKind.Running: running++; break;
				case WorkspaceNoticeKind.Changed: changed++; break;
				case WorkspaceNoticeKind.SameFile: sameFile++; break;
			}
		}

		_card.Meta = _entries.Count == 0
			? string.Empty
			: Strings.WorkspaceNoticeMeta(_entries.Count, running, changed, sameFile);
		_card.Show(_entries.Count > 0);
		return true;
	}

	private void Open(string sessionId) => _open?.Invoke(sessionId);

	// 逐条比值，不用 32 位哈希：碰撞时行里的闭包会留在旧 sessionId 上，点「打开」跳到错会话。
	// 条目通常 1–3 条，逐项比较比逐条算哈希便宜。
	private bool SameEntries(IReadOnlyList<WorkspaceNoticeEntry> entries)
	{
		if (_entries.Count != entries.Count)
		{
			return false;
		}

		for (var index = 0; index < entries.Count; index++)
		{
			if (_entries[index] != entries[index])
			{
				return false;
			}
		}

		return true;
	}
}
