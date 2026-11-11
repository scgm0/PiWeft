using System.Text;

namespace PiWeft.Desktop;

/// 流式文本的追加式快照：只记录「来源 + 代号 + 本次到达的长度」，不复制正文。
/// 打字机、增量 Markdown 与单行预览都只按前缀取窗口；整篇物化留给复制/导出这类冷路径。
/// 为什么需要它：模型每到达一块 delta，视图模型就要重新投影一次行；若每次都把累积的整篇回复
/// <c>StringBuilder.ToString()</c> 一遍，200K 字的回复就是每帧 ~400 KB 的临时字符串
/// （实测占诊断面板「每帧分配」的 99%）。快照把「这一帧的权威文本」表示成
/// <c>(片段表, 长度)</c>：追加只加长度，消费端切片，整篇只在真的需要字符串的地方物化一次。
public sealed class StreamedText
{
	// 片段表只追加、条目不可变；Reset 时整表换新（旧快照继续读旧表，不会被清空的内容绊倒）。
	private readonly List<string> _segments;
	private readonly List<int> _starts;
	private readonly int _segmentCount;
	private readonly int _generation;
	private readonly int _length;
	private string? _materialized;

	internal StreamedText(StreamingBuffer source, int generation)
	{
		_segments = source.Segments;
		_starts = source.Starts;
		_segmentCount = source.SegmentCount;
		_generation = generation;
		_length = source.Length;
	}

	public int Length => _length;

	internal bool Continues(in StreamedText previous) =>
		ReferenceEquals(previous._segments, _segments) && previous._generation == _generation && _length >= previous._length;

	internal bool SameSnapshot(StreamedText? other) =>
		other is not null && ReferenceEquals(other._segments, _segments) && other._generation == _generation &&
		other._length == _length;

	internal char At(int index)
	{
		if ((uint)index >= (uint)_length) return '\0';
		var segment = Locate(index);
		return _segments[segment][index - _starts[segment]];
	}

	public string Materialize() => _materialized ??= Slice(0, _length);

	/// 把区间写进调用方的缓冲，不产生临时字符串（单行预览每帧都要取一次尾部窗口）。
	internal void CopyTo(int start, int count, Span<char> destination)
	{
		start = Math.Clamp(start, 0, _length);
		count = Math.Clamp(count, 0, _length - start);
		if (count > destination.Length) count = destination.Length;
		if (count == 0) return;
		var written = 0;
		var segment = Locate(start);
		while (written < count && segment < _segmentCount)
		{
			var text = _segments[segment];
			var offset = start + written - _starts[segment];
			var take = Math.Min(text.Length - offset, count - written);
			text.AsSpan(offset, take).CopyTo(destination[written..]);
			written += take;
			segment++;
		}
	}

	public string Slice(int start, int count)
	{
		start = Math.Clamp(start, 0, _length);
		count = Math.Clamp(count, 0, _length - start);
		if (count == 0)
		{
			return string.Empty;
		}
		if (start == 0 && count == _length && _materialized is { } cached)
		{
			return cached;
		}
		// string.Create 直接写进最终字符串的存储：少一次 char[] 分配与一次整段拷贝。
		// 片段表与起点表在快照生命周期内不变，因此静态回调可以安全共享。
		return string.Create(count, (Source: this, Start: start), static (span, state) =>
		{
			var written = 0;
			var segment = state.Source.Locate(state.Start);
			while (written < span.Length && segment < state.Source._segmentCount)
			{
				var text = state.Source._segments[segment];
				var offset = state.Start + written - state.Source._starts[segment];
				var take = Math.Min(text.Length - offset, span.Length - written);
				text.AsSpan(offset, take).CopyTo(span[written..]);
				written += take;
				segment++;
			}
		});
	}

	private int Locate(int index)
	{
		var low = 0;
		var high = _segmentCount - 1;
		while (low < high)
		{
			var middle = low + (high - low + 1) / 2;
			if (_starts[middle] <= index) low = middle;
			else high = middle - 1;
		}
		return low;
	}

	public override string ToString() => Materialize();
}

/// 追加式文本缓冲：每次 delta 作为不可变片段入表，绝不回写；快照共享片段表 + 长度。
/// 与 <see cref="StringBuilder"/> 的关键区别是线程安全且历史不可变：视图模型在会话线程追加，
/// UI 线程读旧快照（打字机、增量 Markdown）时不会读到被 Clear 清空的缓冲。
internal sealed class StreamingBuffer
{
	private List<string> _segments = [];
	private List<int> _starts = [];
	private int _length;
	private bool _hasText;

	public int Length => _length;
	public int SegmentCount => _segments.Count;
	/// 是否已经有非空白字符：只追加过空格/换行的缓冲不算有内容。
	public bool HasText => _hasText;
	internal List<string> Segments => _segments;
	internal List<int> Starts => _starts;

	public void Append(string delta)
	{
		if (string.IsNullOrEmpty(delta)) return;
		if (!_hasText) _hasText = !string.IsNullOrWhiteSpace(delta);
		_segments.Add(delta);
		_starts.Add(_length);
		_length += delta.Length;
	}

	public void Reset()
	{
		_segments = [];
		_starts = [];
		_length = 0;
		_hasText = false;
	}
}

/// 文本的两种来源：普通字符串（历史行、工具输出）或流式快照。消费端只依赖这几个操作，
/// 因此同一段「打字机 / 增量 Markdown」代码对两种来源都成立，不必为流式单独维护一份副本。
internal readonly struct StreamText
{
	private readonly string? _plain;
	private readonly StreamedText? _live;

	public StreamText(string text)
	{
		_plain = text;
		_live = null;
	}

	public StreamText(StreamedText text)
	{
		_plain = null;
		_live = text;
	}

	public static StreamText Empty => default;

	public static implicit operator StreamText(string text) => new(text);

	public int Length => _plain?.Length ?? _live?.Length ?? 0;

	public char At(int index) => _plain is not null ? _plain[index] : _live?.At(index) ?? '\0';

	public bool SameAs(in StreamText other)
	{
		if (_plain is not null && other._plain is not null)
		{
			return ReferenceEquals(_plain, other._plain) || string.Equals(_plain, other._plain, StringComparison.Ordinal);
		}
		if (_plain is null && other._plain is null)
		{
			return (_live is null && other._live is null) || (_live is not null && _live.SameSnapshot(other._live));
		}
		return false;
	}

	public bool Continues(in StreamText previous)
	{
		if (_plain is not null && previous._plain is not null)
		{
			return ReferenceEquals(_plain, previous._plain) ||
				_plain.Length >= previous._plain.Length && _plain.StartsWith(previous._plain, StringComparison.Ordinal);
		}
		if (_plain is null && previous._plain is null)
		{
			return _live is not null && previous._live is not null && _live.Continues(previous._live);
		}
		return false;
	}

	public string Slice(int start, int count)
	{
		if (_plain is not null)
		{
			start = Math.Clamp(start, 0, _plain.Length);
			count = Math.Clamp(count, 0, _plain.Length - start);
			return count == 0 ? string.Empty : _plain.Substring(start, count);
		}
		return _live?.Slice(start, count) ?? string.Empty;
	}

	/// 与 <see cref="Slice"/> 同区间，但直接写进调用方缓冲，不做分配。
	internal void CopyTo(int start, int count, Span<char> destination)
	{
		if (_plain is not null)
		{
			start = Math.Clamp(start, 0, _plain.Length);
			count = Math.Clamp(count, 0, _plain.Length - start);
			if (count > destination.Length) count = destination.Length;
			if (count > 0) _plain.AsSpan(start, count).CopyTo(destination);
			return;
		}
		_live?.CopyTo(start, count, destination);
	}

	public string Materialize() => _plain ?? _live?.Materialize() ?? string.Empty;
}
