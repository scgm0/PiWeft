using System.Text;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// 已落盘消息正文的延迟来源：只持有内容块，真的要字符串时才拼接。
/// 工具结果常在几十 KB 量级，投影时拼一次就等于把这段 UTF-16 常驻到时间线里；
/// 表头摘要、行高估算这类有界消费方走 <see cref="Prefix"/>，只解码开头那一小段。
/// 不缓存拼好的整篇：缓存一次就等于把省下的常驻又请回来。
internal sealed class DeferredText
{
	private readonly IReadOnlyList<ContentBlock> _blocks;
	private readonly int _length;

	public DeferredText(IReadOnlyList<ContentBlock> blocks)
	{
		_blocks = blocks;
		var length = 0;
		foreach (var block in blocks)
		{
			if (block is TextContent text)
			{
				length += text.TextLength;
			}
		}

		_length = length;
	}

	public int Length => _length;

	/// 正文为空时不必建来源：空块表比一个对象更省，也让「没有正文」这件事只有一个表示。
	public static DeferredText? Of(IReadOnlyList<ContentBlock>? blocks)
	{
		if (blocks is null || blocks.Count == 0)
		{
			return null;
		}

		var source = new DeferredText(blocks);
		return source._length == 0 ? null : source;
	}

	/// 两份来源内容相同：同一批内容块（消息对象在会话生命周期内不变）就算同一段正文。
	public bool SameAs(DeferredText? other) => other is not null && ReferenceEquals(_blocks, other._blocks);

	public string Materialize()
	{
		if (_length == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder(_length);
		foreach (var block in _blocks)
		{
			if (block is TextContent text)
			{
				builder.Append(text.Text);
			}
		}

		return builder.ToString();
	}

	/// 取区间内的字符，只解码与该区间相交的内容块。
	public string Slice(int start, int count)
	{
		start = Math.Clamp(start, 0, _length);
		count = Math.Clamp(count, 0, _length - start);
		if (count == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder(Math.Min(count, 4096));
		var offset = 0;
		foreach (var block in _blocks)
		{
			if (block is not TextContent text)
			{
				continue;
			}

			var blockLength = text.TextLength;
			if (offset + blockLength <= start)
			{
				offset += blockLength;
				continue;
			}

			var localStart = Math.Max(0, start - offset);
			var take = Math.Min(blockLength - localStart, count - builder.Length);
			var content = localStart == 0
				? text.Prefix(take)
				: take == blockLength ? text.Text : text.Text.Substring(localStart, take);
			builder.Append(content);
			offset += blockLength;
			if (builder.Length >= count)
			{
				break;
			}
		}

		return builder.ToString();
	}

	/// 有界预览：只解码开头需要的那几个字符，不产生整篇字符串。
	public string Prefix(int maxCharacters)
	{
		if (maxCharacters <= 0 || _length == 0)
		{
			return string.Empty;
		}

		var builder = new StringBuilder(Math.Min(_length, maxCharacters));
		foreach (var block in _blocks)
		{
			if (block is not TextContent text)
			{
				continue;
			}

			var need = maxCharacters - builder.Length;
			if (need <= 0)
			{
				break;
			}

			builder.Append(text.Prefix(need));
		}

		return builder.ToString();
	}

	/// 第 <paramref name="index"/> 个字符。随机访问要解码所在块；打字机只用在流式行上，这里不是热路径。
	public char At(int index)
	{
		if ((uint)index >= (uint)_length)
		{
			return '\0';
		}

		var offset = 0;
		foreach (var block in _blocks)
		{
			if (block is not TextContent text)
			{
				continue;
			}

			var blockLength = text.TextLength;
			if (index < offset + blockLength)
			{
				return text.Text[index - offset];
			}

			offset += blockLength;
		}

		return '\0';
	}
}
