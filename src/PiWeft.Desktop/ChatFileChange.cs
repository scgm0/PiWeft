using System.Text;

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
	private readonly List<Entry> _entries = [];
	private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);

	public int Count => _entries.Count;

	public void Clear()
	{
		_entries.Clear();
		_slots.Clear();
	}

	public void Add(string path, int added, int removed, string patch)
	{
		if (_slots.TryGetValue(path, out var slot))
		{
			_entries[slot].Add(added, removed, patch);
			return;
		}

		_slots[path] = _entries.Count;
		_entries.Add(new Entry(path, added, removed, patch));
	}

	/// 当前累计结果的快照。`List.ToArray` 直接拷数组，避免集合表达式在接口上装箱枚举器。
	public ChatFileChange[] Snapshot()
	{
		var result = new ChatFileChange[_entries.Count];
		for (var index = 0; index < result.Length; index++) result[index] = _entries[index].ToChange();
		return result;
	}

	/// 单文件的可变累计器：每次 Add 重建一条 record 会多出与工具调用次数等量的垃圾。
	private sealed class Entry
	{
		private readonly string _path;
		private int _count = 1;
		private int _added;
		private int _removed;
		// 单次修改占多数：直接留住工具结果里的补丁字符串，不复制。
		private string? _singlePatch;
		private StringBuilder? _patches;

		public Entry(string path, int added, int removed, string patch)
		{
			_path = path;
			_added = added;
			_removed = removed;
			if (patch.Length > 0) _singlePatch = patch;
		}

		public void Add(int added, int removed, string patch)
		{
			_count++;
			_added += added;
			_removed += removed;
			if (patch.Length == 0) return;
			if (_singlePatch is null)
			{
				_singlePatch = patch;
				return;
			}

			// 多次修改按顺序拼接：diff 视图逐段显示，不去猜原文件内容。
			// 中间留一个空行，两段补丁的文件头不会粘在一起。
			_patches ??= new StringBuilder(_singlePatch);
			_patches.Append('\n').Append(patch);
		}

		public ChatFileChange ToChange() => new(
			_path, _count, _added, _removed, _patches?.ToString() ?? _singlePatch ?? string.Empty);
	}
}
