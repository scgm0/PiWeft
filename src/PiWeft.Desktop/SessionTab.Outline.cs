using Aprillz.MewUI;

namespace PiWeft.Desktop;

public sealed partial class SessionTab
{
	private readonly List<ConversationOutline.Item> _outlineItems = [];
	private readonly List<(int Row, ChatEntry Entry)> _outlineSources = [];
	private int[] _outlineUserRows = [];
	private bool _outlineActiveQueued;

	/// <summary>
	/// 右侧定位轴只由用户行决定，而 <paramref name="stablePrefix"/> 之前的行投影器已经保证与上一次
	/// 引用一致，所以那里的大纲条目原样留着，只校验尾部、只补新增的用户行。
	/// 以前这里每帧为上千行重建两个列表并整表比较（1000 轮历史约 60 KB/帧的纯浪费），
	/// 流式期最明显的无谓分配就是它。
	/// </summary>
	private void SyncOutline(IReadOnlyList<ChatEntry> entries, int stablePrefix)
	{
		var changed = false;

		// 尾部倒着收：行号越界或引用变了的用户行丢掉，遇到仍然成立的就停下。
		while (_outlineSources.Count > 0 && _outlineSources[^1].Row >= stablePrefix)
		{
			var (row, entry) = _outlineSources[^1];
			if (row < entries.Count && ReferenceEquals(entries[row], entry)) break;
			_outlineSources.RemoveAt(_outlineSources.Count - 1);
			_outlineItems.RemoveAt(_outlineItems.Count - 1);
			changed = true;
		}

		// 从最后一条已确认的用户行往后补：token 帧通常一行都不用扫。
		var from = _outlineSources.Count > 0 ? _outlineSources[^1].Row + 1 : 0;
		for (var row = from; row < entries.Count; row++)
		{
			if (entries[row].Role != ChatRole.User) continue;
			_outlineSources.Add((row, entries[row]));
			_outlineItems.Add(new ConversationOutline.Item(row, OutlineTitle(entries[row]), OutlineTime(entries[row].Timestamp)));
			changed = true;
		}

		if (changed)
		{
			_outlineUserRows = [.. _outlineItems.Select(item => item.RowIndex)];
			_outline.SetItems(_outlineItems);
		}

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
