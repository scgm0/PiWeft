using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Text;

namespace PiWeft.Desktop;

public static class DiffView
{
	private const double LargeGutterWidth = 52;

	public static UIElement Render(string patch) => Render(patch, null);

	internal static UIElement Render(string patch, TextFocusScrollGuard? focus)
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

		var large = MessageText.IsLargeText(normalized);
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
			AppStyles.RefreshTextView(view);
		});
		focus?.Track(text);
		// 大补丁的行号列也限高；滚动条是覆盖绘制的，留宽一点避免压住数字。
		var gutter = MessageText.Create(string.Join('\n', numbers), mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(large ? LargeGutterWidth : 40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		if (large)
		{
			// 正文滚动时用 MakeVisible 把行号推到同一位置，避免两列错行。
			gutter.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);
			SyncVertical(text, gutter);
		}
		var horizontal = new ScrollViewer().BorderThickness(0).Background(AppTheme.Transparent)
			.Content(text).AutoHorizontalScroll().NoVerticalScroll().SmoothScrolling();
		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new DockPanel().LastChildFill().Children(gutter.DockLeft(), horizontal));
	}

	/// 并排 diff：左旧右新两列，行数一致逐行对齐；每列可独立横向滚动，纵向共用同步偏移（大补丁走内部限高视口）。
	/// hunk 头渲染成两列都显示的分隔行。
	internal static UIElement RenderSideBySide(string patch, TextFocusScrollGuard? focus)
	{
		var rows = SideBySideDiff.Parse(patch);
		if (rows.Count == 0)
		{
			return Render(patch, focus);
		}

		var width = Math.Max(220, AppTheme.MessageMaxWidth / 2 - 12);
		var oldText = string.Join('\n', rows.Select(row => Line(row, true)));
		var newText = string.Join('\n', rows.Select(row => Line(row, false)));
		var large = MessageText.IsLargeText(oldText) || MessageText.IsLargeText(newText);
		var left = SideColumn(rows, old: true, width, focus, oldText, large);
		var right = SideColumn(rows, old: false, width, focus, newText, large);
		if (large) SyncSideColumns(left, right);
		var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.Children(left.View, right.View);

		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new ScrollViewer().Content(content).AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling());
	}

	private static SideColumnView SideColumn(IReadOnlyList<DiffRow> rows, bool old, double width, TextFocusScrollGuard? focus,
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
			AppStyles.RefreshTextView(view);
		});
		focus?.Track(body);

		var gutter = MessageText.Create(numbers, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(large ? LargeGutterWidth : 40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		if (large) gutter.MaxHeight(MessageText.LargeTextMaxHeight).VerticalAlignment(VerticalAlignment.Top);

		var view = new Border().Width(width)
			.Child(new DockPanel().LastChildFill().Children(
				gutter.DockLeft(),
				new ScrollViewer().BorderThickness(0).Background(AppTheme.Transparent)
					.Content(body).AutoHorizontalScroll().NoVerticalScroll().SmoothScrolling()));
		return new SideColumnView(view, body, gutter);
	}

	private readonly record struct SideColumnView(Border View, MultiLineTextBox Body, MultiLineTextBox Gutter);

	private static void SyncVertical(MultiLineTextBox source, MultiLineTextBox target)
	{
		source.ScrollOffsetChanged += _ =>
		{
			// 传目标自己的视口高度和 X：MakeVisible 只改纵向位置，横向滚动仍由正文自己保留。
			var height = target.TextViewportBounds.Height;
			if (height <= 0) height = source.TextViewportBounds.Height;
			target.MakeVisible(new Rect(target.ScrollOffset.X, source.ScrollOffset.Y, 1, height));
		};
	}

	private static void SyncSideColumns(SideColumnView left, SideColumnView right)
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
				Align(left.Body, source);
				Align(right.Body, source);
				Align(left.Gutter, source);
				Align(right.Gutter, source);
			}
			finally
			{
				syncing = false;
			}
		}

		static void Align(MultiLineTextBox target, MultiLineTextBox source)
		{
			if (ReferenceEquals(target, source)) return;
			var height = target.TextViewportBounds.Height;
			if (height <= 0) height = source.TextViewportBounds.Height;
			target.MakeVisible(new Rect(target.ScrollOffset.X, source.ScrollOffset.Y, 1, height));
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
