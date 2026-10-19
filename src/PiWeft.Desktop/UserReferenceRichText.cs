using System.Runtime.CompilerServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;
using Aprillz.MewUI.Text.Editing;

namespace PiWeft.Desktop;

/// <summary>
/// 让 <see cref="MultiLineTextBox"/> 把 <c>@"path"</c> / <c>@path</c> 画成真正的行内 chip。
/// 原始文档仍是完整路径（发送、复制、撤销都不改），投影层把引用压成一个占位字符，
/// 元素生成器在这个占位区间上放 <see cref="IInlineTextObject"/>；编辑时再把整枚 chip 当作原子。
/// </summary>
internal static class UserReferenceRichText
{
	private const char Placeholder = '\uFFFC';
	private const double ChipHeight = 20;
	private const double IconSize = 12;
	private const double LabelMaxWidth = 220;
	private const double PaddingLeft = 6;
	private const double PaddingRight = 7;
	private const double IconGap = 4;

	private static readonly ConditionalWeakTable<MultiLineTextBox, object> Attached = [];

	public static void Attach(MultiLineTextBox box, bool editable, Action<string>? onOpen = null)
	{
		if (Attached.TryGetValue(box, out _)) return;
		Attached.Add(box, new object());
		box.Extensions.Projections.Add(new ReferenceProjection());
		box.Extensions.ElementGenerators.Add(new ReferenceElementGenerator());
		box.Extensions.Revision++;
		if (editable)
		{
			box.EditableRegions = new ReferenceEditableRegions(box);
			box.TextCompositionStart += _ => NormalizeCaret(box);
		}
		string? hint = null;
		box.MouseMove += args =>
		{
			if (args.LeftButton) return;
			var reference = ReferenceAtPoint(box, args.GetPosition(box));
			box.Cursor = reference is null ? CursorType.IBeam : CursorType.Hand;
			var path = reference?.Path;
			if (path == hint) return;
			hint = path;
			// 悬浮在 chip 上时把完整路径交给 HoverHints；锚在整个文本框下沿，
			// 不额外造一套浮层，主题与边界翻转仍由现有 Popup 负责。
			HoverHints.Set(box, path);
		};
		box.MouseLeave += () =>
		{
			box.Cursor = CursorType.IBeam;
			if (hint is null) return;
			hint = null;
			HoverHints.Set(box, null);
		};
		if (onOpen is not null)
		{
			box.MouseUp += args =>
			{
				if (args.Button != MouseButton.Left || box.SelectionLength > 0) return;
				if (ReferenceAtPoint(box, args.GetPosition(box)) is not { } reference) return;
				args.Handled = true;
				onOpen(reference.Path);
			};
		}
	}

	/// <summary>引用在草稿里的数量（诊断与契约测试用）。</summary>
	public static int Count(string text) => UserFileReferences.Parse(text).Count;

	/// <summary>把落在 chip 内部的选区扩到整枚 chip；没有引用时返回 null。</summary>
	public static TextRange? ExpandSelection(MultiLineTextBox box)
	{
		if (box.SelectionLength == 0) return null;
		var references = UserFileReferences.Parse(box.Text);
		if (references.Count == 0) return null;
		var start = box.SelectionStart;
		var end = start + box.SelectionLength;
		var expandedStart = start;
		var expandedEnd = end;
		foreach (var reference in references)
		{
			if (reference.End <= start || reference.Start >= end) continue;
			expandedStart = Math.Min(expandedStart, reference.Start);
			expandedEnd = Math.Max(expandedEnd, reference.End);
		}
		return expandedStart == start && expandedEnd == end ? null : new TextRange(expandedStart, expandedEnd - expandedStart);
	}

	/// <summary>粘贴/替换前把选区或光标移出 chip 内部，避免编辑落在不可见的原文里。</summary>
	public static void NormalizeForEdit(MultiLineTextBox box)
	{
		if (ExpandSelection(box) is { } expanded)
		{
			box.Select(expanded.Start, expanded.Length);
			box.MoveCaret(expanded.Start + expanded.Length, extendSelection: true);
			return;
		}
		if (box.SelectionLength == 0) NormalizeCaret(box);
	}

	/// <summary>命中测试：鼠标点是否落在一枚已投影的 chip 上。</summary>
	internal static UserFileReference? ReferenceAtPoint(MultiLineTextBox box, Point point)
	{
		var content = box.TextViewportBounds;
		var local = new Point(point.X - content.X, point.Y - content.Y);
		foreach (var line in box.VisibleTextLines)
		{
			if (!line.VisualLines.Any(visual => visual.Bounds.Contains(local))) continue;
			var lineText = box.Text.Substring(line.LogicalLine.Offset, line.LogicalLine.Length);
			foreach (var reference in UserFileReferences.Parse(lineText))
			{
				var start = line.MapSourceOffsetToProjected(reference.Start);
				var end = line.MapSourceOffsetToProjected(reference.End);
				if (end <= start) continue;
				var rects = new List<Rect>();
				line.GetRangeBounds(new TextRange(start, end - start), rects);
				foreach (var rect in rects)
				{
					var document = new Rect(line.DocumentX + rect.X, line.DocumentY + rect.Y, rect.Width, rect.Height);
					if (document.Contains(local)) return reference;
				}
			}
		}
		return null;
	}

	private static void NormalizeCaret(MultiLineTextBox box)
	{
		var references = UserFileReferences.Parse(box.Text);
		var caret = box.CaretPosition;
		foreach (var reference in references)
		{
			if (caret <= reference.Start || caret >= reference.End) continue;
			// 光标落在 chip 内部时贴到最近的边；真实编辑仍以整枚 chip 为单位。
			box.MoveCaret(caret - reference.Start <= reference.End - caret ? reference.Start : reference.End, false);
			return;
		}
	}

	/// <summary>方向键、Backspace/Delete 在 chip 边界上按整枚跳转/删除。</summary>
	public static bool TryHandleKey(MultiLineTextBox box, KeyEventArgs key)
	{
		if (box.IsReadOnly) return false;
		var references = UserFileReferences.Parse(box.Text);
		if (references.Count == 0) return false;

		if (key.Key is Key.Left or Key.Right && (key.ShiftKey || box.SelectionLength == 0))
		{
			var caret = box.CaretPosition;
			var reference = key.Key == Key.Right
				? references.FirstOrDefault(item => caret >= item.Start && caret < item.End)
				: references.LastOrDefault(item => caret > item.Start && caret <= item.End);
			if (reference is not null)
			{
				box.MoveCaret(key.Key == Key.Right ? reference.End : reference.Start, key.ShiftKey);
				key.Handled = true;
				return true;
			}
		}

		if (key.Key is not (Key.Backspace or Key.Delete) || key.AltKey) return false;
		if (box.SelectionLength > 0)
		{
			if (ExpandSelection(box) is not { } expanded) return false;
			box.ReplaceRange(expanded.Start, expanded.Length, null);
			box.MoveCaret(expanded.Start, false);
			key.Handled = true;
			return true;
		}

		var position = box.CaretPosition;
		var target = key.Key == Key.Backspace
			? references.LastOrDefault(item => position > item.Start && position <= item.End)
			: references.FirstOrDefault(item => position >= item.Start && position < item.End);
		if (target is null) return false;
		box.ReplaceRange(target.Start, target.End - target.Start, null);
		box.MoveCaret(target.Start, false);
		key.Handled = true;
		return true;
	}

	/// <summary>输入文字时如果选区只覆盖 chip 的一部分，先扩成整枚再替换。</summary>
	public static bool TryHandleTextInput(MultiLineTextBox box, TextInputEventArgs input)
	{
		if (box.IsReadOnly || string.IsNullOrEmpty(input.Text)) return false;
		var references = UserFileReferences.Parse(box.Text);
		if (references.Count == 0) return false;

		if (box.SelectionLength > 0)
		{
			var expanded = ExpandSelection(box);
			if (expanded is null) return false;
			box.EnterText(expanded.Value.Start, expanded.Value.Length, input.Text);
			input.Handled = true;
			return true;
		}

		var caret = box.CaretPosition;
		var inside = references.FirstOrDefault(item => caret > item.Start && caret < item.End);
		if (inside is null) return false;
		box.EnterText(inside.End, 0, input.Text);
		input.Handled = true;
		return true;
	}

	private sealed class ReferenceProjection : ITextProjection
	{
		public ProjectedText Project(in TextProjectionContext context)
		{
			if (!context.SourceText.Span.Contains('@'))
			{
				return new(context.SourceText, IdentityTextOffsetMap.Instance);
			}
			var source = context.SourceText.ToString();
			var references = UserFileReferences.Parse(source);
			if (references.Count == 0) return new(source.AsMemory(), IdentityTextOffsetMap.Instance);

			var projectedLength = source.Length;
			foreach (var reference in references) projectedLength -= reference.End - reference.Start - 1;
			var projected = new char[projectedLength];
			var sourceToProjected = new int[source.Length + 1];
			var projectedToSource = new int[projectedLength + 1];

			var sourceIndex = 0;
			var projectedIndex = 0;
			foreach (var reference in references)
			{
				while (sourceIndex < reference.Start)
				{
					projected[projectedIndex] = source[sourceIndex];
					sourceToProjected[sourceIndex] = projectedIndex;
					projectedToSource[projectedIndex] = sourceIndex;
					sourceIndex++;
					projectedIndex++;
				}

				sourceToProjected[reference.Start] = projectedIndex;
				var length = reference.End - reference.Start;
				for (var offset = 1; offset < length; offset++)
				{
					// 光标落在 chip 内部时贴向最近的一侧，不出现“藏在路径里”的插入点。
					sourceToProjected[reference.Start + offset] = offset <= length / 2 ? projectedIndex : projectedIndex + 1;
				}
				sourceToProjected[reference.End] = projectedIndex + 1;
				projectedToSource[projectedIndex] = reference.Start;
				projectedToSource[projectedIndex + 1] = reference.End;
				projected[projectedIndex] = Placeholder;
				sourceIndex = reference.End;
				projectedIndex++;
			}

			while (sourceIndex < source.Length)
			{
				projected[projectedIndex] = source[sourceIndex];
				sourceToProjected[sourceIndex] = projectedIndex;
				projectedToSource[projectedIndex] = sourceIndex;
				sourceIndex++;
				projectedIndex++;
			}
			sourceToProjected[source.Length] = projectedLength;
			projectedToSource[projectedLength] = source.Length;

			return new ProjectedText(new string(projected).AsMemory(), new ReferenceOffsetMap(sourceToProjected, projectedToSource));
		}
	}

	private sealed class ReferenceOffsetMap(int[] sourceToProjected, int[] projectedToSource) : ITextOffsetMap
	{
		public int MapToSource(int projectedOffset)
			=> projectedToSource[Math.Clamp(projectedOffset, 0, projectedToSource.Length - 1)];

		public int MapFromSource(int sourceOffset)
			=> sourceToProjected[Math.Clamp(sourceOffset, 0, sourceToProjected.Length - 1)];
	}

	private sealed class ReferenceElementGenerator : ITextElementGenerator
	{
		public int GetFirstInterestedOffset(in TextElementScanContext context, int startOffset)
		{
			if (context.Document.TextLength == 0) return -1;
			var line = context.Document.GetLineByOffset(Math.Clamp(startOffset, 0, context.Document.TextLength));
			if (!HasAt(context.Document, line.Offset, line.Length)) return -1;
			var references = UserFileReferences.Parse(context.Document.GetText(line.Offset, line.Length));
			foreach (var reference in references)
			{
				var start = line.Offset + reference.Start;
				if (start >= startOffset) return start;
			}
			return -1;
		}

		public GeneratedTextElement? ConstructElement(in TextElementScanContext context, int offset)
		{
			var line = context.Document.GetLineByOffset(Math.Clamp(offset, 0, context.Document.TextLength));
			if (!HasAt(context.Document, line.Offset, line.Length)) return null;
			var references = UserFileReferences.Parse(context.Document.GetText(line.Offset, line.Length));
			foreach (var reference in references)
			{
				if (line.Offset + reference.Start != offset) continue;
				return new GeneratedTextElement(
					reference.End - reference.Start,
					1,
					new InlineChip(reference),
					BreaksLine: true);
			}
			return null;
		}

		private static bool HasAt(IReadOnlyTextDocument document, int offset, int length)
		{
			for (var index = offset; index < offset + length; index++)
			{
				if (document.GetCharAt(index) == '@') return true;
			}
			return false;
		}
	}

	/// <summary>把原始路径整段视为可编辑单元；插入点不能落进 chip 内部。</summary>
	private sealed class ReferenceEditableRegions(MultiLineTextBox box) : IEditableRegionProvider
	{
		public bool CanInsert(int offset)
		{
			var references = UserFileReferences.Parse(box.Text);
			foreach (var reference in references)
			{
				if (offset > reference.Start && offset < reference.End) return false;
			}
			return true;
		}

		public void GetDeletableRanges(TextRange range, IList<TextRange> output)
		{
			// 选区删除/剪切仍由 key/text 处理器扩成整枚 chip；这里允许一次连续替换。
			output.Add(range);
		}
	}

	private sealed class InlineChip(UserFileReference reference) : IInlineTextObject
	{
		private ITextLayout? _labelLayout;
		private ITextEngine? _labelEngine;

		public InlineMetrics Measure()
		{
			var width = LabelWidth();
			return new InlineMetrics(PaddingLeft + IconSize + IconGap + width + PaddingRight, ChipHeight, 15);
		}

		public void Draw(ITextRenderContext context, Point origin)
		{
			var info = AppTheme.Info;
			var width = Measure().Width;
			context.Graphics.FillRoundedRectangle(
				new Rect(origin.X, origin.Y, width, ChipHeight), AppTheme.RadiusXs, AppTheme.RadiusXs, info.WithAlpha(0x1f));

			var iconOrigin = new Point(origin.X + PaddingLeft, origin.Y + (ChipHeight - IconSize) / 2);
			if (reference.IsDirectory) DrawFolderIcon(context.Graphics, iconOrigin, info);
			else DrawFileIcon(context.Graphics, iconOrigin, info);

			if (EnsureLabelLayout() is not { } layout) return;
			var textOrigin = new Point(
				origin.X + PaddingLeft + IconSize + IconGap,
				origin.Y + (ChipHeight - layout.MeasuredSize.Height) / 2);
			context.Draw(layout, textOrigin, new TextDrawOptions(info));
		}

		private double LabelWidth() => EnsureLabelLayout()?.MeasuredSize.Width ?? Math.Min(reference.Display.Length * 7.2, LabelMaxWidth);

		private ITextLayout? EnsureLabelLayout()
		{
			if (!Application.IsRunning) return _labelLayout;
			var engine = Application.Current.GraphicsFactory.TextEngine;
			if (_labelLayout is not null && ReferenceEquals(engine, _labelEngine)) return _labelLayout;
			_labelEngine = engine;
			_labelLayout = engine.CreateLayout(new TextLayoutRequest
			{
				Text = reference.Display.AsMemory(),
				DefaultStyle = new TextRunStyle(AppFonts.DefaultUi, AppTheme.FontSizeMicro, FontWeight.Medium),
				Paragraph = new TextParagraphStyle
				{
					MaxWidth = LabelMaxWidth,
					Wrapping = TextWrapping.NoWrap,
					Trimming = TextTrimming.CharacterEllipsis,
				},
			});
			return _labelLayout;
		}

		private static void DrawFileIcon(IGraphicsContext graphics, Point origin, Color color)
		{
			graphics.DrawRoundedRectangle(new Rect(origin.X + 2, origin.Y + 1, 8, 10), 1, 1, color, 1.1);
			graphics.DrawLine(new Point(origin.X + 7, origin.Y + 1), new Point(origin.X + 7, origin.Y + 4), color, 1.1);
			graphics.DrawLine(new Point(origin.X + 7, origin.Y + 4), new Point(origin.X + 10, origin.Y + 4), color, 1.1);
		}

		private static void DrawFolderIcon(IGraphicsContext graphics, Point origin, Color color)
		{
			graphics.DrawRoundedRectangle(new Rect(origin.X + 1, origin.Y + 5, 10, 6), 1, 1, color, 1.1);
			graphics.DrawLine(new Point(origin.X + 1, origin.Y + 5), new Point(origin.X + 1, origin.Y + 3), color, 1.1);
			graphics.DrawLine(new Point(origin.X + 1, origin.Y + 3), new Point(origin.X + 5, origin.Y + 3), color, 1.1);
			graphics.DrawLine(new Point(origin.X + 5, origin.Y + 3), new Point(origin.X + 6, origin.Y + 5), color, 1.1);
		}
	}
}
