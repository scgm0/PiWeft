namespace PiWeft.Desktop;

/// 快捷消息：一句话口令，点条目插入输入框，行尾按钮直接发送。
/// 清单存在 <c>desktop.json</c> 的 <c>quickMessages</c> 键里，与置顶、最近项目同一份偏好文件；
/// 清洗规则放这里，偏好读写、弹层与设置分区共用一份，避免「界面收得下、落盘被截掉」。
public static class QuickMessages
{
	/// 上界：弹框一页 8 条、最多 4 页；再多就该写成提示词模板。
	public const int MaxItems = 30;

	/// 单条长度上限：快捷消息是一句话，整段任务描述属于提示词模板。
	public const int MaxLength = 200;

	/// 每页条数：8 行压在一屏内可扫，出厂 20 条正好三页。
	public const int PageSize = 8;

	/// 出厂清单，与 PiDeck 的 <c>resources/quick-messages.default.json</c> 同一条目顺序。
	public static IReadOnlyList<string> Defaults { get; } =
	[
		"你好，你是什么模型",
		"你好",
		"请问你是什么模型",
		"继续",
		"提交",
		"推送",
		"提交推送",
		"随便用ask提问我",
		"用中文回答我",
		"给出完整可运行的文件",
		"只改我提到的部分，不要顺手改其它代码",
		"参考项目里已有的实现和代码风格",
		"不要添加不必要的注释",
		"请仔细思考，确保正确且完整实现",
		"再确认一下",
		"还是不对，请重新排查",
		"还是无法运行，先看报错再修",
		"先检索查证再回答，不要凭猜测下结论",
		"先别写代码，先梳理方案",
		"review 一下这个 commit",
	];

	/// 清洗条目：去首尾空白、丢空行、按上限截断、大小写无关去重，最后裁到 <see cref="MaxItems"/>。
	/// 截断而不是丢弃：用户写长句是真的想用，只是超过了界面的量级。
	public static IReadOnlyList<string> Normalize(IEnumerable<string?>? items)
	{
		if (items is null)
		{
			return Array.Empty<string>();
		}

		var result = new List<string>(MaxItems);
		var seen = new HashSet<string>(MaxItems, StringComparer.OrdinalIgnoreCase);
		foreach (var raw in items)
		{
			Accumulate(raw.AsSpan(), result, seen);
			if (result.Count == MaxItems)
			{
				break;
			}
		}

		return result;
	}

	/// 设置分区里的「一行一条」格式：按换行切成 span 逐条清洗，不先造一遍行数组。
	public static IReadOnlyList<string> ParseLines(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return Array.Empty<string>();
		}

		var result = new List<string>(MaxItems);
		var seen = new HashSet<string>(MaxItems, StringComparer.OrdinalIgnoreCase);
		var lines = text.AsSpan();
		foreach (var range in lines.Split('\n'))
		{
			Accumulate(lines[range], result, seen);
			if (result.Count == MaxItems)
			{
				break;
			}
		}

		return result;
	}

	/// 把清单写成设置分区里的一行一条文本。
	public static string ToLines(IEnumerable<string> items) => string.Join('\n', items);

	/// 插入草稿尾：已有内容换行再接，条目保留原文（与 PiDeck 的 appendContentToDraft 同义）。
	/// 刻意不剥 frontmatter 或做命令展开：用户点的是原文，就原样进输入框。
	public static string Append(string draft, string item)
	{
		var text = item.Trim();
		if (text.Length == 0)
		{
			return draft;
		}

		var head = draft.TrimEnd();
		return head.Length == 0 ? text : string.Concat(head, "\n", text);
	}

	/// 搜索：去首尾空白 + 大小写无关子串匹配。不做模糊匹配：条目都是短句，
	/// 模糊命中会把「提交推送」拆成一堆结果。
	public static IReadOnlyList<string> Filter(IReadOnlyList<string> items, string? query)
	{
		var needle = query?.Trim();
		if (string.IsNullOrEmpty(needle))
		{
			return items;
		}

		var matches = new List<string>(Math.Min(items.Count, MaxItems));
		foreach (var item in items)
		{
			if (item.Contains(needle, StringComparison.OrdinalIgnoreCase))
			{
				matches.Add(item);
			}
		}

		// 全部命中时返回原列表：短查询下每次按键都复制一遍没有意义，
		// 后面的分页也能直接复用同一份清单。
		return matches.Count == items.Count ? items : matches;
	}

	/// 分页：页码越界时夹紧到 [1, 总页数]，空清单算 1 页。
	/// 越界是正常操作（搜完清空搜索、设置里删条）导致的，必须自愈，不能返回空页。
	public static (IReadOnlyList<string> Items, int Page, int TotalPages) Paginate(
		IReadOnlyList<string> items, int page, int pageSize = PageSize)
	{
		var size = Math.Max(1, pageSize);
		var totalPages = Math.Max(1, (items.Count + size - 1) / size);
		var clamped = Math.Clamp(page, 1, totalPages);
		var start = (clamped - 1) * size;
		var count = Math.Min(size, items.Count - start);

		// 整份清单都在这一页：直接复用原列表，搜索时不再为每次按键造一个页数组。
		if (start == 0 && count == items.Count)
		{
			return (items, clamped, totalPages);
		}

		if (count <= 0)
		{
			return (Array.Empty<string>(), clamped, totalPages);
		}

		var pageItems = new string[count];
		for (var index = 0; index < count; index++)
		{
			pageItems[index] = items[start + index];
		}

		return (pageItems, clamped, totalPages);
	}

	private static void Accumulate(ReadOnlySpan<char> line, List<string> result, HashSet<string> seen)
	{
		var trimmed = line.Trim();
		if (trimmed.IsEmpty)
		{
			return;
		}

		if (trimmed.Length > MaxLength)
		{
			// 恰好切在代理对中间会留下半个字符，界面上是不可见的坏码位。
			var length = char.IsHighSurrogate(trimmed[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
			trimmed = trimmed[..length];
		}

		var text = trimmed.ToString();
		if (seen.Add(text))
		{
			result.Add(text);
		}
	}
}
