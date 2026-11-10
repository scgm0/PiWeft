namespace PiWeft.Desktop;

/// 一轮里同一个文件的改动汇总：成功写入次数、增删行数与合并后的统一补丁。
/// 补丁供轮末 chip 打开 diff 浮层用；历史会话缺补丁时为空串，chip 仍显示次数与行数。
/// <param name="Path">工具参数里的原始路径，可能是相对路径。</param>
/// <param name="Count">本轮成功写入该文件的工具调用次数。</param>
/// <param name="Added">本轮新增行数合计。</param>
/// <param name="Removed">本轮删除行数合计。</param>
/// <param name="Patch">本轮所有改动的统一补丁，多次修改按顺序拼接。</param>
public sealed record ChatFileChange(string Path, int Count, int Added, int Removed, string Patch)
{
	/// 轮末 chip 上显示的文件名；同名文件靠悬停完整路径区分。
	public string FileName => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
}

/// 一轮内按路径累计文件改动。ChatViewModel 在每个回合重建时复用同一个实例，
/// 路径查找走字典，避免几十次工具调用各扫一遍列表。
internal sealed class TurnFileChanges
{
	private readonly List<ChatFileChange> _items = [];
	private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);

	public IReadOnlyList<ChatFileChange> Items => _items;

	public int Count => _items.Count;

	public void Clear()
	{
		_items.Clear();
		_slots.Clear();
	}

	public void Add(string path, int added, int removed, string patch)
	{
		if (_slots.TryGetValue(path, out var slot))
		{
			var current = _items[slot];
			_items[slot] = current with
			{
				Count = current.Count + 1,
				Added = current.Added + added,
				Removed = current.Removed + removed,
				Patch = Join(current.Patch, patch),
			};
			return;
		}

		_slots[path] = _items.Count;
		_items.Add(new ChatFileChange(path, 1, added, removed, patch));
	}

	/// 多次修改同一文件时按顺序拼接补丁：diff 视图逐段显示，不去猜原文件内容。
	/// 中间留一个空行，两段补丁的文件头不会粘在一起。
	private static string Join(string first, string second)
	{
		if (first.Length == 0) return second;
		if (second.Length == 0) return first;
		return first + "\n" + second;
	}
}
