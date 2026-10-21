using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Text;

namespace PiWeft.Desktop;

/// <summary>Selectable unified diff with a separate line-number gutter and paint-only classification.</summary>
public static class DiffView
{
	public static UIElement Render(string patch) => Render(patch, null);

	internal static UIElement Render(string patch, TextFocusScrollGuard? focus)
	{
		var normalized = patch.Replace("\r\n", "\n").Replace('\r', '\n');
		var numbers = new List<string>();
		var oldLine = 0;
		var newLine = 0;
		var lines = normalized.Split('\n');
		for (var index = 0; index < lines.Length; index++)
		{
			var line = lines[index];
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				(oldLine, newLine) = ParseHunkHeader(line, oldLine, newLine);
				numbers.Add(string.Empty);
			}
			else if (IsMetadata(line) || index == lines.Length - 1 && line.Length == 0) numbers.Add(string.Empty);
			else if (line.StartsWith('-')) numbers.Add((oldLine++).ToString());
			else
			{
				numbers.Add((newLine++).ToString());
				if (!line.StartsWith('+')) oldLine++;
			}
		}

		// One document allows dragging a selection across added, deleted and context lines.
		var text = MessageText.Create(normalized, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro);
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
		var gutter = MessageText.Create(string.Join('\n', numbers), mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro)
			.Width(40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;
		var horizontal = new ScrollViewer().BorderThickness(0).Background(AppTheme.Transparent)
			.Content(text).AutoHorizontalScroll().NoVerticalScroll().SmoothScrolling();
		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new DockPanel().LastChildFill().Children(gutter.DockLeft(), horizontal));
	}

	private static bool IsMetadata(string line) => line.StartsWith("+++", StringComparison.Ordinal) ||
		line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("diff ", StringComparison.Ordinal) ||
		line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith('\\');

	/// <summary>
	/// 并排 diff：左旧右新两列，行数一致逐行对齐；每列可独立横向滚动，纵向共用外层滚动。
	/// hunk 头渲染成两列都显示的分隔行。
	/// </summary>
	internal static UIElement RenderSideBySide(string patch, TextFocusScrollGuard? focus)
	{
		var rows = SideBySideDiff.Parse(patch);
		if (rows.Count == 0)
		{
			return Render(patch, focus);
		}

		var width = Math.Max(220, AppTheme.MessageMaxWidth / 2 - 12);
		var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.Children(SideColumn(rows, old: true, width, focus), SideColumn(rows, old: false, width, focus));

		return new Border().StyleName(AppStyles.Code)
			.WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(1).CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space2, 4, AppTheme.Space2, 4))
			.Child(new ScrollViewer().Content(content).AutoVerticalScroll().NoHorizontalScroll().SmoothScrolling());
	}

	private static UIElement SideColumn(IReadOnlyList<DiffRow> rows, bool old, double width, TextFocusScrollGuard? focus)
	{
		var numbers = string.Join('\n', rows.Select(row => (old ? row.OldNumber : row.NewNumber)?.ToString() ?? string.Empty));
		var text = string.Join('\n', rows.Select(row => Line(row, old)));

		var body = MessageText.Create(text, mono: true, wrap: false, fontSize: AppTheme.FontSizeMicro);
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
			.Width(40).Focusable(false).IsHitTestVisible(false);
		gutter.WithTheme((theme, block) => block.Foreground = AppTheme.For(theme).TextFaint);
		gutter.Tag = null;

		return new Border().Width(width)
			.Child(new DockPanel().LastChildFill().Children(
				gutter.DockLeft(),
				new ScrollViewer().BorderThickness(0).Background(AppTheme.Transparent)
					.Content(body).AutoHorizontalScroll().NoVerticalScroll().SmoothScrolling()));
	}

	/// <summary>生成并排某一侧的文本行；缺失的一侧为空行（保留一个空格以保持行高）。</summary>
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

	internal static (int OldLine, int NewLine) ParseHunkHeader(string line, int fallbackOld, int fallbackNew)
	{
		var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");
		return match.Success && int.TryParse(match.Groups[1].Value, out var oldLine) && int.TryParse(match.Groups[2].Value, out var newLine)
			? (oldLine, newLine) : (fallbackOld, fallbackNew);
	}

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
