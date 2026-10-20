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

	public static void Attach(MultiLineTextBox box, bool editable, Action<string>? onOpen = null, string? cwd = null,
		Func<string, string?>? skillLocation = null)
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
		IUserChip? hint = null;
		box.MouseMove += args =>
		{
			if (args.LeftButton) return;
			var hit = ChipAtPoint(box, args.GetPosition(box));
			// 只有文件 chip 能点击打开；技能 chip 只是行内标记，保持文本光标。
			box.Cursor = hit is { Chip.Kind: UserChipKind.File } ? CursorType.Hand : CursorType.IBeam;
			var chip = hit?.Chip;
			if (Equals(chip, hint)) return;
			hint = chip;
			// 悬浮在 chip 上时把完整路径 / 技能位置交给 HoverHints，锚点就用这枚 chip 的矩形，
			// 长消息里浮层也贴着鼠标；主题与边界翻转仍由现有 Popup 负责。
			HoverHints.Set(box, chip is null ? null : HintFor(chip, cwd, skillLocation), hit?.Bounds);
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

	/// <summary>chip 的悬浮提示：文件给完整路径，技能给 SKILL.md 的位置（解析不到时回到命令原文）。</summary>
	internal static string HintFor(IUserChip chip, string? cwd, Func<string, string?>? skillLocation)
	{
		if (chip is UserSkillReference skill)
		{
			if (skillLocation?.Invoke(skill.Name) is { Length: > 0 } location) return location;
			return skill.Command;
		}
		return FullPath(chip.Value, cwd);
	}

	/// <summary>chip 提示里的完整路径：相对引用按会话工作目录解析成绝对路径。</summary>
	internal static string FullPath(string path, string? cwd)
	{
		if (string.IsNullOrEmpty(cwd) || Path.IsPathRooted(path)) return path;
		return MessageLinkTarget.Resolve(path, cwd)?.Target ?? path;
	}

	/// <summary>行内 chip（文件引用 + 技能调用）的数量（诊断与契约测试用）。</summary>
	public static int Count(string text) => UserChips.Parse(text).Count;

	/// <summary>把落在 chip 内部的选区扩到整枚 chip；没有 chip 时返回 null。</summary>
	public static TextRange? ExpandSelection(MultiLineTextBox box)
	{
		if (box.SelectionLength == 0) return null;
		var references = UserChips.Parse(box.Text);
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

	/// <summary>
	/// 命中测试：<paramref name="point"/> 是文本框内（<c>MouseEventArgs.GetPosition(box)</c>）的相对坐标，
	/// 落在一枚已投影的文件 chip 上时返回它（技能 chip 不可点击，只走 <see cref="ChipAtPoint"/>）。
	/// </summary>
	internal static UserFileReference? ReferenceAtPoint(MultiLineTextBox box, Point point)
		=> ChipAtPoint(box, point)?.Chip as UserFileReference;

	private static ChipHit? ChipAtPoint(MultiLineTextBox box, Point point)
	{
		// Bounds / TextViewportBounds 都是窗口坐标，而文档坐标以内容视口左上角为原点；
		// 先把窗口坐标的视口原点换算到文本框局部坐标，再和文档坐标相减。
		var content = box.TextViewportBounds;
		var origin = new Point(content.X - box.Bounds.X, content.Y - box.Bounds.Y);
		var local = new Point(point.X - origin.X, point.Y - origin.Y);
		foreach (var line in box.VisibleTextLines)
		{
			if (!line.VisualLines.Any(visual => visual.Bounds.Contains(local))) continue;
			var lineText = box.Text.Substring(line.LogicalLine.Offset, line.LogicalLine.Length);
			foreach (var chip in UserChips.Parse(lineText))
			{
				var start = line.MapSourceOffsetToProjected(chip.Start);
				var end = line.MapSourceOffsetToProjected(chip.End);
				if (end <= start) continue;
				var rects = new List<Rect>();
				line.GetRangeBounds(new TextRange(start, end - start), rects);
				foreach (var rect in rects)
				{
					var document = new Rect(line.DocumentX + rect.X, line.DocumentY + rect.Y, rect.Width, rect.Height);
					if (!document.Contains(local)) continue;
					// chip 的窗口矩形：悬浮提示直接锚在它下面，而不是整个文本框。
					return new ChipHit(chip, new Rect(content.X + document.X, content.Y + document.Y, document.Width, document.Height));
				}
			}
		}
		return null;
	}

	private readonly record struct ChipHit(IUserChip Chip, Rect Bounds);

	private static void NormalizeCaret(MultiLineTextBox box)
	{
		var references = UserChips.Parse(box.Text);
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
		var references = UserChips.Parse(box.Text);
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
		var references = UserChips.Parse(box.Text);
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
			var source = context.SourceText.ToString();
			if (!source.Contains('@') && !source.Contains("/skill:", StringComparison.Ordinal))
			{
				return new(source.AsMemory(), IdentityTextOffsetMap.Instance);
			}
			var references = UserChips.Parse(source);
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
			var lineText = context.Document.GetText(line.Offset, line.Length);
			if (!MayContainChip(lineText)) return -1;
			foreach (var chip in UserChips.Parse(lineText))
			{
				var start = line.Offset + chip.Start;
				if (start >= startOffset) return start;
			}
			return -1;
		}

		public GeneratedTextElement? ConstructElement(in TextElementScanContext context, int offset)
		{
			var line = context.Document.GetLineByOffset(Math.Clamp(offset, 0, context.Document.TextLength));
			var lineText = context.Document.GetText(line.Offset, line.Length);
			if (!MayContainChip(lineText)) return null;
			foreach (var chip in UserChips.Parse(lineText))
			{
				if (line.Offset + chip.Start != offset) continue;
				return new GeneratedTextElement(
					chip.End - chip.Start,
					1,
					new InlineChip(chip),
					BreaksLine: true);
			}
			return null;
		}

		private static bool MayContainChip(string text)
			=> text.Contains('@') || text.Contains("/skill:", StringComparison.Ordinal);
	}

	/// <summary>把原始路径 / 技能命令整段视为可编辑单元；插入点不能落进 chip 内部。</summary>
	private sealed class ReferenceEditableRegions(MultiLineTextBox box) : IEditableRegionProvider
	{
		public bool CanInsert(int offset)
		{
			var references = UserChips.Parse(box.Text);
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

	private sealed class InlineChip(IUserChip chip) : IInlineTextObject
	{
		private ITextLayout? _labelLayout;
		private ITextEngine? _labelEngine;

		/// <summary>技能用 Thinking 紫（和气泡上方的技能 chip 同色），文件引用用 Info 蓝。</summary>
		private Color Accent => chip.Kind == UserChipKind.Skill ? AppTheme.Thinking : AppTheme.Info;

		public InlineMetrics Measure()
		{
			var width = LabelWidth();
			return new InlineMetrics(PaddingLeft + IconSize + IconGap + width + PaddingRight, ChipHeight, 15);
		}

		public void Draw(ITextRenderContext context, Point origin)
		{
			var accent = Accent;
			var width = Measure().Width;
			context.Graphics.FillRoundedRectangle(
				new Rect(origin.X, origin.Y, width, ChipHeight), AppTheme.RadiusXs, AppTheme.RadiusXs, accent.WithAlpha(0x1f));

			var iconOrigin = new Point(origin.X + PaddingLeft, origin.Y + (ChipHeight - IconSize) / 2);
			if (chip.Kind == UserChipKind.Skill) DrawSparklesIcon(context.Graphics, iconOrigin, accent);
			else if (chip.IsDirectory) DrawFolderIcon(context.Graphics, iconOrigin, accent);
			else DrawFileIcon(context.Graphics, iconOrigin, accent);

			if (EnsureLabelLayout() is not { } layout) return;
			var textOrigin = new Point(
				origin.X + PaddingLeft + IconSize + IconGap,
				origin.Y + (ChipHeight - layout.MeasuredSize.Height) / 2);
			context.Draw(layout, textOrigin, new TextDrawOptions(accent));
		}

		private double LabelWidth() => EnsureLabelLayout()?.MeasuredSize.Width ?? Math.Min(chip.Display.Length * 7.2, LabelMaxWidth);

		private ITextLayout? EnsureLabelLayout()
		{
			if (!Application.IsRunning) return _labelLayout;
			var engine = Application.Current.GraphicsFactory.TextEngine;
			if (_labelLayout is not null && ReferenceEquals(engine, _labelEngine)) return _labelLayout;
			_labelEngine = engine;
			_labelLayout = engine.CreateLayout(new TextLayoutRequest
			{
				Text = chip.Display.AsMemory(),
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

		/// <summary>四角星 + 右上小十字（对齐 lucide sparkles 的轮廓），12px 下仍能认出是技能。</summary>
		private static void DrawSparklesIcon(IGraphicsContext graphics, Point origin, Color color)
		{
			var cx = origin.X + 5.4;
			var cy = origin.Y + 7.4;
			const double radius = 4.6;
			// 控制点沿对角线外移一点，花瓣比纯中心控制更饱满，12px 下不会细成一条线。
			var waist = radius * 0.16;
			var star = new PathGeometry();
			star.MoveTo(cx, cy - radius);
			star.QuadTo(cx + waist, cy - waist, cx + radius, cy);
			star.QuadTo(cx + waist, cy + waist, cx, cy + radius);
			star.QuadTo(cx - waist, cy + waist, cx - radius, cy);
			star.QuadTo(cx - waist, cy - waist, cx, cy - radius);
			star.Close();
			graphics.FillPath(star, color);

			// 右上角的小十字，和 lucide sparkles 的辅助星一致。
			graphics.DrawLine(new Point(cx + 3.6, cy - 6.0), new Point(cx + 3.6, cy - 3.0), color, 1.0);
			graphics.DrawLine(new Point(cx + 2.1, cy - 4.5), new Point(cx + 5.1, cy - 4.5), color, 1.0);
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
