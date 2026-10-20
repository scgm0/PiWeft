using Aprillz.MewUI;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	private IReadOnlyList<ConversationOutline.Item> _outlineItems = [];
	private readonly List<(int Row, ChatEntry Entry)> _outlineSources = [];
	private int[] _outlineUserRows = [];
	private bool _outlineActiveQueued;

	private void SyncOutline(IReadOnlyList<ChatEntry> entries)
	{
		var items = new List<ConversationOutline.Item>();
		var sources = new List<(int Row, ChatEntry Entry)>();
		for (var row = 0; row < entries.Count; row++)
		{
			var entry = entries[row];
			if (entry.Role != ChatRole.User) continue;
			var ordinal = items.Count;
			// 流式帧不重新解析历史问题里的技能上下文，也不反复格式化时间。
			var unchanged = ordinal < _outlineSources.Count && _outlineSources[ordinal].Row == row &&
				ReferenceEquals(_outlineSources[ordinal].Entry, entry);
			items.Add(unchanged ? _outlineItems[ordinal] : new(row, OutlineTitle(entry), OutlineTime(entry.Timestamp)));
			sources.Add((row, entry));
		}
		if (!_outlineItems.SequenceEqual(items))
		{
			_outlineItems = items;
			_outlineUserRows = items.Select(item => item.RowIndex).ToArray();
			_outline.SetItems(items);
		}
		_outlineSources.Clear();
		_outlineSources.AddRange(sources);
		UpdateOutlineVisibility();
		QueueOutlineActive();
	}

	private void UpdateOutlineVisibility() => _outline.SetVisible(_messages.IsVisible && _outlineItems.Count > 1 &&
		(_mainArea.Bounds.Width == 0 || _mainArea.Bounds.Width >= 540));

	private void JumpToOutline(int index)
	{
		if (index < 0 || index >= _messages.ItemsSource.Count) return;
		_conversationScroll.Browse();
		_outline.SetActiveRow(index);
		_timeline.JumpToRow(index);
		QueueOutlineActive();
	}

	private void OutlineScrolled()
	{
		_outline.HidePreview();
		QueueOutlineActive();
	}

	private void QueueOutlineActive()
	{
		if (_disposed || _outlineActiveQueued) return;
		if (!Application.IsRunning || Application.Current.Dispatcher is not { } dispatcher)
		{
			UpdateOutlineActive();
			return;
		}
		// ScrollChanged 可能在 Arrange 中发出；等虚拟化容器排完再读实际行位置。
		_outlineActiveQueued = true;
		dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
		{
			_outlineActiveQueued = false;
			if (!_disposed) UpdateOutlineActive();
		});
	}

	private void UpdateOutlineActive()
	{
		// 最后一问可能很短，无法对齐视口顶部；物理底部明确对应最后一枚刻度。
		if (_outlineUserRows.Length > 0 && SmoothScroll.For(_scroll).MaxVerticalOffset - _scroll.VerticalOffset <= 1)
		{
			_outline.SetActiveRow(_outlineUserRows[^1]);
			return;
		}
		var anchorRow = _timeline.RowAtViewportAnchor();
		if (anchorRow < 0) return;
		// 锚点常常在很长的助手回复/过程行内，原问题已被虚拟化回收。
		// 从「任意角色的当前行」倒找前一条问题，而不是把下一条可见问题误认成当前项。
		var ordinal = OutlineRailTicks.ActiveIndex(_outlineUserRows, anchorRow);
		_outline.SetActiveRow(ordinal >= 0 ? _outlineUserRows[ordinal] : -1);
	}

	internal static string OutlineTitle(ChatEntry entry)
	{
		var (skills, body) = UserMessageText.Parse(entry.Text);
		foreach (var line in body.Split('\n'))
		{
			var value = line.Trim();
			if (value.Length > 0) return value.Length <= 120 ? value : value[..120] + "…";
		}
		if (skills.Count > 0) return string.Join("、", skills.Select(skill => skill.Name));
		return entry.Images.Count > 0 ? Strings.OutlineImageMessage : Strings.RoleUser;
	}

	private static string OutlineTime(long timestamp) => timestamp > 0
		? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime().ToString("MM/dd HH:mm")
		: string.Empty;
}
