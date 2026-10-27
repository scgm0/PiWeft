namespace PiWeft.Desktop;

/// <summary>
/// Git 面板某一节的可调高度与展开态（对齐 PiDeck 的 pane state）。
/// 折叠时这一节只剩节头，所以不需要高度；展开时高度由用户拖动决定，并夹在上下限之间。
/// </summary>
internal readonly record struct GitPaneLayout(
	double Graph,
	double Compare,
	double More,
	bool ChangesOpen,
	bool GraphOpen,
	bool CompareOpen,
	bool MoreOpen)
{
	/// <summary>节头高度：与 <c>GitView</c> 里的节头一致，折叠后就剩这么高。</summary>
	public const double HeaderHeight = 30;

	/// <summary>展开后正文的最小高度：再小就只看得见一行，不如直接折叠。</summary>
	public const double MinBody = 60;

	/// <summary>默认只展开「更改」与「图」，比较与更多收着 —— 面板宽度有限，全铺开谁都用不上。</summary>
	public static GitPaneLayout Default { get; } = new(220, 160, 140, true, true, false, false);

	/// <summary>展开态下这一节的总高（节头 + 正文）。</summary>
	public static double ExpandedHeight(double body) =>
		HeaderHeight + Math.Max(MinBody, body);

	/// <summary>将某一节的正文高度夹紧（同时保证整组不会把主区挤没：上限由宿主给出）。</summary>
	public static double ClampBody(double body, double max) =>
		Math.Clamp(body, MinBody, Math.Max(MinBody, max));

	/// <summary>
	/// 落盘格式：<c>图=200:1|比较=160:0|更多=140:0|更改=1</c>（正文高度:展开）。
	/// 用短键而不是 JSON：这条记录按仓库存在 desktop.json 的一个字符串值里，读的人要能一眼看懂。
	/// </summary>
	public string Encode() =>
		$"图={Graph:0}:{(GraphOpen ? 1 : 0)}|比较={Compare:0}:{(CompareOpen ? 1 : 0)}|" +
		$"更多={More:0}:{(MoreOpen ? 1 : 0)}|更改={(ChangesOpen ? 1 : 0)}";

	public static GitPaneLayout Decode(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return Default;
		}

		var layout = Default;
		foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var equal = part.IndexOf('=');
			if (equal <= 0)
			{
				continue;
			}

			var key = part[..equal].Trim();
			var value = part[(equal + 1)..].Trim();

			if (key == "更改")
			{
				layout = layout with { ChangesOpen = value == "1" };
				continue;
			}

			var colon = value.IndexOf(':');
			if (colon <= 0 || !double.TryParse(value[..colon], out var body) || body <= 0)
			{
				continue;
			}

			var open = value.Length > colon + 1 && value[(colon + 1)..] == "1";
			layout = key switch
			{
				"图" => layout with { Graph = body, GraphOpen = open },
				"比较" => layout with { Compare = body, CompareOpen = open },
				"更多" => layout with { More = body, MoreOpen = open },
				_ => layout,
			};
		}

		return layout with
		{
			Graph = Math.Clamp(layout.Graph, MinBody, 900),
			Compare = Math.Clamp(layout.Compare, MinBody, 900),
			More = Math.Clamp(layout.More, MinBody, 900),
		};
	}

	public GitPaneLayout WithGraph(double body, bool open) =>
		this with { Graph = Math.Round(body), GraphOpen = open };

	public GitPaneLayout WithCompare(double body, bool open) =>
		this with { Compare = Math.Round(body), CompareOpen = open };

	public GitPaneLayout WithMore(double body, bool open) =>
		this with { More = Math.Round(body), MoreOpen = open };

	public GitPaneLayout WithChanges(bool open) => this with { ChangesOpen = open };
}
