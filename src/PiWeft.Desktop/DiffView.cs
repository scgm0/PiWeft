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
		var numbers = new List<string>();
		var oldLine = 0;
		var newLine = 0;
		// 逐行走 span：一份几千行的补丁用 Split('\n') 会白白分配几千个短字符串，
		// 而行号列只需要每行一个计数。枚举结果与 Split 一致（含结尾空行）。
		var lineCount = normalized.Length == 0 ? 1 : normalized.AsSpan().Count('\n') + 1;
		var index = 0;
		foreach (var line in normalized.AsSpan().EnumerateLines())
		{
			var last = ++index == lineCount;
			if (line.StartsWith("@@"))
			{
				(oldLine, newLine) = DiffSyntax.ParseHunkHeader(line, oldLine, newLine);
				numbers.Add(string.Empty);
			}
			else if (DiffSyntax.IsMetadata(line) || last && line.IsEmpty) numbers.Add(string.Empty);
			else if (line.StartsWith("-")) numbers.Add((oldLine++).ToString());
			else
			{
				numbers.Add((newLine++).ToString());
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
		// 大补丁行号到四五位，留宽一点。
		var gutter = MessageText.Create(string.Join('\n', numbers), mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(large ? LargeGutterWidth : 40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		HideScrollBars(gutter);
		if (large) gutter.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);
		// 行号列是影子视口：正文滚到哪，它用 MakeVisible 跟到哪，避免两列错行。
		SyncVertical(text, gutter);
		// 正文直接吃 DockPanel 给的宽高：高度由宿主决定（浮层是窗口高度，工具行是上限），
		// 文本控件按这个高度收口并只物化可见行，纵向滚动条落在正文自己的右缘。
		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new DockPanel().LastChildFill().Children(gutter.DockLeft(), text));
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

		var oldText = string.Join('\n', rows.Select(row => Line(row, true)));
		var newText = string.Join('\n', rows.Select(row => Line(row, false)));
		var large = capLargeText && (MessageText.IsLargeText(oldText) || MessageText.IsLargeText(newText));
		var left = SideColumn(rows, old: true, focus, oldText, large);
		var right = SideColumn(rows, old: false, focus, newText, large);
		SyncColumns(left, right);
		// 两列等分宽度：列宽跟着宿主走，不固定在阅读宽度的一半上。
		var content = new Grid().Columns("*,*").Spacing(AppTheme.Space2)
			.Children(left.View.Column(0), right.View.Column(1));

		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(content);
	}

	private static SideColumnView SideColumn(IReadOnlyList<DiffRow> rows, bool old, TextFocusScrollGuard? focus,
		string text, bool large)
	{
		var numbers = string.Join('\n', rows.Select(row => (old ? row.OldNumber : row.NewNumber)?.ToString() ?? string.Empty));

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
			.Width(large ? LargeGutterWidth : 40).Focusable(false).IsHitTestVisible(false);
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

	private static string Line(DiffRow row, bool old)
	{
		if (row.Kind == DiffRowKind.Header)
		{
			return row.Header;
		}

		if ((old ? row.OldNumber : row.NewNumber) is null)
		{
			return " ";
		}

		var marker = row.Kind switch
		{
			DiffRowKind.Context => " ",
			DiffRowKind.Change => old ? "-" : "+",
			DiffRowKind.Deletion => "-",
			DiffRowKind.Addition => "+",
			_ => " ",
		};

		return marker + (old ? row.OldText : row.NewText);
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
