using System.Text;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Text;

namespace PiWeft.Desktop;

public static class DiffView
{
	private const double LargeGutterWidth = 52;

	public static UIElement Render(string patch) => Render(patch, null);

	/// <param name="capLargeText">大补丁是否限高 240。对话里的工具行用（避免一行 diff 撑满会话），
	/// 差异浮层不用：卡片按窗口高度收口，正文自己滚。</param>
	internal static UIElement Render(string patch, TextFocusScrollGuard? focus, bool capLargeText = true)
	{
		var normalized = patch.Replace("\r\n", "\n").Replace('\r', '\n');
		var oldLine = 0;
		var newLine = 0;
		var lineCount = normalized.Length == 0 ? 1 : normalized.AsSpan().Count('\n') + 1;
		// 行号列直接写进 StringBuilder：几千行的补丁不再为每行分配一个计数串。
		// 容量按每行 5 位数字加换行估；长行补丁按字符数估会多占一大截。
		var gutterText = new StringBuilder((int)Math.Min(normalized.Length / 4L, lineCount * 6L));
		var firstGutterLine = true;
		void NextGutterLine()
		{
			if (!firstGutterLine) gutterText.Append('\n');
			firstGutterLine = false;
		}

		var index = 0;
		foreach (var line in normalized.AsSpan().EnumerateLines())
		{
			var last = ++index == lineCount;
			if (line.StartsWith("@@"))
			{
				(oldLine, newLine) = DiffSyntax.ParseHunkHeader(line, oldLine, newLine);
				NextGutterLine();
			}
			else if (DiffSyntax.IsMetadata(line) || last && line.IsEmpty) NextGutterLine();
			else if (line.StartsWith("-"))
			{
				NextGutterLine();
				gutterText.Append(oldLine++);
			}
			else
			{
				NextGutterLine();
				gutterText.Append(newLine++);
				if (!line.StartsWith("+")) oldLine++;
			}
		}

		var large = capLargeText && MessageText.IsLargeText(normalized);
		// One document allows dragging a selection across added, deleted and context lines.
		var text = MessageText.Create(normalized, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro);
		if (large) text.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);
		text.Foreground = AppTheme.TextMuted;
		text.Tag = null;
		text.Extensions.Classifiers.Add(new DiffClassifier());
		text.InvalidateTextView();
		// 换肤后重新分类一次：语义色取当前配色，否则深色那一套会留在浅色代码底上。
		text.WithTheme((theme, view) =>
		{
			view.Foreground = AppTheme.For(theme).TextMuted;
			// 选中后统一换成主题的选区文字色：diff 里的 ---/+++ 路径是 TextFaint，
			// 深色主题的灰色选区（#5a）几乎吞掉它。
			view.SelectionForeground = theme.Palette.SelectionText;
			AppStyles.RefreshTextView(view);
		});
		focus?.Track(text);
		// 行号到六位才加宽：行号列的滚动条已隐藏，40 能放下五位数字。
		var gutterWidth = Math.Max(oldLine, newLine) > 99_999 ? LargeGutterWidth : 40;
		var gutter = MessageText.Create(gutterText.ToString(), mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(gutterWidth).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		HideScrollBars(gutter);
		if (large) gutter.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);
		// 行号列是影子视口：正文滚到哪，它用 MakeVisible 跟到哪，避免两列错行。
		SyncVertical(text, gutter);
		// 正文直接吃 DockPanel 给的宽高：高度由宿主决定（浮层是窗口高度，工具行是上限），
		// 文本控件按这个高度收口并只物化可见行，纵向滚动条落在正文自己的右缘。
		var view = new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new DockPanel().LastChildFill().Children(gutter.DockLeft(), text));
		RouteGutterWheel(view, text);
		return view;
	}

	/// 并排 diff：左旧右新两列，行数一致逐行对齐；hunk 头渲染成两列都显示的分隔行。
	/// 每列是一个可选中、可横向滚动的整篇文本控件：列宽由宿主均分，长行在列内横向滚动，
	/// 两列的横纵偏移同步到同一个绝对值（滚一列，另一列跟着走），行号列固定在左缘。
	/// 正文自己按宿主给的高度收口，只物化可见行。
	/// <param name="capLargeText">同 <see cref="Render"/>：工具行限高 240，浮层按窗口高度。</param>
	internal static UIElement RenderSideBySide(string patch, TextFocusScrollGuard? focus, bool capLargeText = true)
	{
		var rows = SideBySideDiff.Parse(patch);
		if (rows.Count == 0)
		{
			return Render(patch, focus, capLargeText);
		}

		// 两列正文与行号列一次遍历写进 StringBuilder：几万行的并排补丁不再为每行
		// 分配中间串，也不再枚举 rows 四遍。
		var oldText = new StringBuilder(patch.Length / 2);
		var newText = new StringBuilder(patch.Length / 2);
		var oldNumbers = new StringBuilder(rows.Count * 3);
		var newNumbers = new StringBuilder(rows.Count * 3);
		var maxNumber = 0;
		for (int rowIndex = 0, rowCount = rows.Count; rowIndex < rowCount; rowIndex++)
		{
			var row = rows[rowIndex];
			if (rowIndex > 0)
			{
				oldText.Append('\n');
				newText.Append('\n');
				oldNumbers.Append('\n');
				newNumbers.Append('\n');
			}

			AppendSideLine(oldText, row, old: true);
			AppendSideLine(newText, row, old: false);
			if (row.OldNumber is { } oldNumber)
			{
				oldNumbers.Append(oldNumber);
				maxNumber = Math.Max(maxNumber, oldNumber);
			}

			if (row.NewNumber is { } newNumber)
			{
				newNumbers.Append(newNumber);
				maxNumber = Math.Max(maxNumber, newNumber);
			}
		}

		// 两列行数相同，行数按 rows 判断（N 行文本有 N-1 个换行，与 IsLargeText 的 >= 判定对齐）；
		// 字符数按缓冲长度，省一次扫描。
		var large = capLargeText && (oldText.Length >= MessageText.LargeTextCharacters ||
			newText.Length >= MessageText.LargeTextCharacters || rows.Count > MessageText.LargeTextLines);
		var wideGutter = maxNumber > 99_999;
		var left = SideColumn(focus, oldText.ToString(), oldNumbers.ToString(), large, wideGutter);
		var right = SideColumn(focus, newText.ToString(), newNumbers.ToString(), large, wideGutter);
		SyncColumns(left, right);
		// 两列等分宽度：列宽跟着宿主走，不固定在阅读宽度的一半上。
		var content = new Grid().Columns("*,*").Spacing(AppTheme.Space2)
			.Children(left.View.Column(0), right.View.Column(1));

		var view = new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(content);
		RouteGutterWheel(view, left.Body);
		return view;
	}

	private static SideColumnView SideColumn(TextFocusScrollGuard? focus, string text, string numbers, bool large,
		bool wideGutter)
	{
		var body = MessageText.Create(text, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro);
		if (large) body.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);
		body.Foreground = AppTheme.TextMuted;
		body.Tag = null;
		body.Extensions.Classifiers.Add(new DiffClassifier());
		body.InvalidateTextView();
		body.WithTheme((theme, view) =>
		{
			view.Foreground = AppTheme.For(theme).TextMuted;
			view.SelectionForeground = theme.Palette.SelectionText;
			AppStyles.RefreshTextView(view);
		});
		focus?.Track(body);

		var gutter = MessageText.Create(numbers, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(wideGutter ? LargeGutterWidth : 40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		HideScrollBars(gutter);
		if (large) gutter.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);

		// 行号列固定宽度：横向滚动只动正文，行号留在左缘。
		var view = new Grid().Columns("Auto,*").Children(
			gutter.Column(0),
			body.Column(1));
		return new SideColumnView(view, body, gutter);
	}

	private readonly record struct SideColumnView(Grid View, MultiLineTextBox Body, MultiLineTextBox Gutter);

	private static void SyncVertical(MultiLineTextBox source, MultiLineTextBox target)
	{
		source.ScrollOffsetChanged += _ =>
		{
			// 传目标自己的 X：MakeVisible 只改纵向位置，横向滚动仍由正文自己保留。
			var height = target.TextViewportBounds.Height;
			if (height <= 0) height = source.TextViewportBounds.Height;
			if (height <= 0) return;
			target.MakeVisible(new Rect(target.ScrollOffset.X, source.ScrollOffset.Y, 1, height));
		};
	}

	/// 行号列与内边距不参与命中测试，滚轮落在那里时正文收不到；这里把同一条滚轮
	/// 交给正文的平滑控制器。正文自己命中时已处理过，不会重复。到边界时控制器会按
	/// 嵌套视口继续往外滚（对话里的工具行仍能滚会话）。
	private static void RouteGutterWheel(Border view, MultiLineTextBox body) =>
		view.MouseWheel += args => SmoothScroll.For(body).RouteWheel(args);

	/// 行号列不参与命中测试，但它也是可滚动文本：视口被夹住时会画一条自己的滚动条，
	/// 拖不动又和正文那条并排。ScrollBarVisibility 没有 Hidden，只能把绘制压到 0；
	/// 布局与命中不受影响，行号列本来也不吃指针。
	private static void HideScrollBars(MultiLineTextBox text)
	{
		((IVisualTreeHost)text).VisitChildren(child =>
		{
			if (child is ScrollBar bar) bar.RenderOpacity = 0;
			return true;
		});
	}

	/// 两列的横纵偏移同步到同一个绝对值：读一条长行时滚一列，另一列跟着走，
	/// 行号与两侧内容始终在同一列位置。空一侧的内容更短时会被自身上限夹住，属于预期。
	private static void SyncColumns(SideColumnView left, SideColumnView right)
	{
		var syncing = false;
		left.Body.ScrollOffsetChanged += _ => Sync(left.Body);
		right.Body.ScrollOffsetChanged += _ => Sync(right.Body);

		void Sync(MultiLineTextBox source)
		{
			if (syncing) return;
			syncing = true;
			try
			{
				AlignBoth(left.Body, source);
				AlignBoth(right.Body, source);
				// 行号列只跟纵向：横向位置必须留在左缘。
				AlignVertical(left.Gutter, source);
				AlignVertical(right.Gutter, source);
			}
			finally
			{
				syncing = false;
			}
		}

		static void AlignBoth(MultiLineTextBox target, MultiLineTextBox source)
		{
			if (ReferenceEquals(target, source)) return;
			var viewport = target.TextViewportBounds;
			if (viewport.Width <= 0 || viewport.Height <= 0) return;
			// 视口大小的矩形：起点就是要对齐的偏移，比目标靠前或靠后都会被拉回同一点。
			target.MakeVisible(new Rect(source.ScrollOffset.X, source.ScrollOffset.Y, viewport.Width, viewport.Height));
		}

		static void AlignVertical(MultiLineTextBox target, MultiLineTextBox source)
		{
			if (ReferenceEquals(target, source)) return;
			var viewport = target.TextViewportBounds;
			if (viewport.Height <= 0) return;
			target.MakeVisible(new Rect(target.ScrollOffset.X, source.ScrollOffset.Y, 1, viewport.Height));
		}
	}

	/// 把一行并排文本追加到列缓冲。空行占位、hunk 头与标记的规则与统一视图一致。
	private static void AppendSideLine(StringBuilder builder, DiffRow row, bool old)
	{
		if (row.Kind == DiffRowKind.Header)
		{
			builder.Append(row.Header);
			return;
		}

		if ((old ? row.OldNumber : row.NewNumber) is null)
		{
			builder.Append(' ');
			return;
		}

		builder.Append(row.Kind switch
		{
			DiffRowKind.Context => ' ',
			DiffRowKind.Change => old ? '-' : '+',
			DiffRowKind.Deletion => '-',
			DiffRowKind.Addition => '+',
			_ => ' ',
		});
		builder.Append(old ? row.OldText : row.NewText);
	}

	internal static (int OldLine, int NewLine) ParseHunkHeader(string line, int fallbackOld, int fallbackNew) =>
		DiffSyntax.ParseHunkHeader(line, fallbackOld, fallbackNew);

	internal sealed class DiffClassifier : ITextClassifier
	{
		public void Classify(in TextClassificationContext context, IList<TextPaintSpan> output)
		{
			var text = context.Text.Span;
			if (text.IsEmpty) return;
			// 代码表面在浅色主题下是浅底，diff 的语义色必须取当前配色，不能写死深色一套。
			var color = text.StartsWith("@@", StringComparison.Ordinal) ? AppTheme.TextPrimary :
				text.StartsWith("+++", StringComparison.Ordinal) || text.StartsWith("---", StringComparison.Ordinal) ? AppTheme.TextFaint :
				text[0] == '+' ? AppTheme.Success : text[0] == '-' ? AppTheme.Error : AppTheme.TextMuted;
			output.Add(new TextPaintSpan(new TextRange(0, text.Length), color));
		}
	}
}
