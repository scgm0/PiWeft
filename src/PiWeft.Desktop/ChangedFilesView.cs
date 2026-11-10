using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 文件扩展名到 Lucide 图标与颜色的映射。
/// 图标是列表项的身份标记：一屏里 .cs 与 .md 一眼分开，比统一一个 file-text 好扫。
internal static class FileTypeIcons
{
	/// 返回图标名与配色角色；未知扩展名退回 file-text。
	public static (string Icon, string Role) For(string path)
	{
		var extension = Path.GetExtension(path).ToLowerInvariant();
		return extension switch
		{
			".cs" or ".csx" or ".fs" or ".fsx" or ".vb" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs"
				or ".py" or ".rb" or ".go" or ".rs" or ".java" or ".kt" or ".kts" or ".swift" or ".c" or ".h"
				or ".cc" or ".cpp" or ".hpp" or ".m" or ".mm" or ".php" or ".lua" or ".dart" or ".scala" or ".vue"
				or ".svelte" or ".razor" or ".css" or ".scss" or ".less" or ".html" or ".htm"
				=> ("file-code", AppStyles.RoleInfo),
			".json" or ".jsonc" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".xml"
				or ".csproj" or ".fsproj" or ".slnx" or ".props" or ".targets" or ".editorconfig"
				=> ("braces", AppStyles.RoleWarning),
			".sh" or ".bash" or ".zsh" or ".fish" or ".ps1" or ".bat" or ".cmd" or ".sql"
				=> ("terminal", AppStyles.RoleSuccess),
			".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" or ".ico" or ".tiff"
				=> ("file-image", AppStyles.RoleSuccess),
			_ => ("file-text", AppStyles.RoleSubtle),
		};
	}
}

/// 轮末「本轮修改的文件」：一行说明文字 + 一行文件 chip。
/// 说明文字保留轮次语境（这是本轮产物），chip 给出文件名、改写次数与增删行数。
/// 点击 chip 打开该文件的 diff 浮层；悬停显示完整路径（同名文件靠它区分）。
internal sealed class ChangedFilesView
{
	private readonly TextBlock _caption = new();
	private readonly FileChipRow _row;
	private readonly StackPanel _view;
	private IReadOnlyList<ChatFileChange>? _shown;
	private int _count = -1;

	public ChangedFilesView()
	{
		_caption.FontSize(AppTheme.FontSizeMicro).SubtleText().VerticalAlignment(VerticalAlignment.Center);
		_row = new FileChipRow();
		_view = new StackPanel().Spacing(AppTheme.Space1).Children(_caption, _row);
	}

	public UIElement View => _view;

	public bool IsVisible
	{
		get => _view.IsVisible;
		set => _view.IsVisible(value);
	}

	/// 内容没变就不重建 chip：行被回收后重新挂回来、换肤重绘都会走到这里，重建会让行高抖动。
	public void Update(IReadOnlyList<ChatFileChange> files, Func<ChatFileChange, string> hint, Action<ChatFileChange>? openDiff)
	{
		if (files.Count != _count)
		{
			_count = files.Count;
			_caption.Text(Strings.ChangedFilesCaption(files.Count));
		}

		if (_shown is { } shown && ReferenceEquals(shown, files)) return;
		_shown = files;
		_row.SetFiles(files, hint, openDiff);
	}
}

/// 单行文件 chip 容器：折叠态只排一行，放不下的 chip 隐藏并把数量交给 +N；展开态按可用宽度换行。
/// 隐藏的 chip 保留上一次量到的宽度，窗口变宽后能重新出现，不必先重新测量全部 chip。
internal sealed class FileChipRow : Panel
{
	private const double Gap = AppTheme.Space1;
	private const double ChipHeight = 22;
	private const double ChipNameWidth = 220;

	private readonly List<Button> _chips = [];
	private readonly List<double> _widths = [];
	private readonly TextBlock _moreLabel;
	private readonly GlyphElement _moreGlyph;
	private readonly Button _more;
	// 装箱每次测量都要回调：缓存方法组，别让每趟布局都分配一个委托。
	private readonly Func<int, double> _moreWidth;
	private bool _expanded;
	private int _visible;
	private bool _moreVisible;
	private double _chipHeight;

	public FileChipRow()
	{
		// 折叠态隐藏的 chip 不会被排布，这一层剪裁是防像素取整带来的半像素溢出。
		ClipToBounds = true;
		_moreLabel = new TextBlock().FontSize(AppTheme.FontSizeMicro).FontWeight(FontWeight.Medium)
			.VerticalAlignment(VerticalAlignment.Center);
		_moreGlyph = new GlyphElement().Kind(GlyphKind.ChevronDown).GlyphSize(3)
			.VerticalAlignment(VerticalAlignment.Center);
		_more = new Button()
			.StyleName(AppStyles.ChangedFilesMore)
			.Padding(new Thickness(6, 2, 7, 2))
			.MinHeight(ChipHeight)
			.Content(new StackPanel()
				.Orientation(Orientation.Horizontal)
				.Spacing(4)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(_moreLabel, _moreGlyph))
			.OnClick(Toggle);
		_moreWidth = MoreWidth;
	}

	public void SetFiles(IReadOnlyList<ChatFileChange> files, Func<ChatFileChange, string> hint, Action<ChatFileChange>? openDiff)
	{
		foreach (var chip in _chips)
		{
			Remove(chip);
			UiLifetime.Release(chip);
		}

		_chips.Clear();
		_widths.Clear();
		_expanded = false;
		_visible = 0;
		_moreVisible = false;
		_chipHeight = 0;
		foreach (var file in files)
		{
			var chip = Chip(file, hint(file), openDiff);
			_chips.Add(chip);
			_widths.Add(double.NaN);
			Add(chip);
		}

		// +N 始终排在最后：chip 重建后要重新挂到末尾。
		Remove(_more);
		if (files.Count > 0) Add(_more);
		InvalidateMeasure();
	}

	internal bool IsExpanded => _expanded;

	internal Button MoreButton => _more;

	protected override Size MeasureContent(Size availableSize)
	{
		if (_chips.Count == 0) return new Size(0, 0);

		var width = double.IsFinite(availableSize.Width) ? Math.Max(0.0, availableSize.Width) : double.PositiveInfinity;
		MeasureChips(availableSize.Height);

		if (_expanded)
		{
			SetMore(Strings.ChangedFilesCollapse, GlyphKind.ChevronUp, visible: true);
			for (var index = 0; index < _chips.Count; index++) Show(_chips[index], true);
			MeasureChips(availableSize.Height);
			_more.Measure(new Size(double.PositiveInfinity, availableSize.Height));
			_visible = _chips.Count;
			return WrapSize(width);
		}

		// 量 +N 前先让它可测；宽度算完再按是否真的溢出决定显隐。
		_more.IsVisible = true;
		var count = FileChipFit.Fit(CollectionsMarshal.AsSpan(_widths), Gap, width, _moreWidth);
		var hidden = _chips.Count - count;
		_visible = count;
		for (var index = 0; index < _chips.Count; index++) Show(_chips[index], index < count);
		SetMore(Strings.ChangedFilesMore(hidden), GlyphKind.ChevronDown, visible: hidden > 0);
		return new Size(Math.Min(width, UsedWidth(count)), RowHeight());
	}

	protected override void ArrangeContent(Rect bounds)
	{
		if (_chips.Count == 0) return;
		if (!_expanded)
		{
			var x = bounds.X;
			var height = RowHeight();
			for (var index = 0; index < _visible; index++)
			{
				_chips[index].Arrange(new Rect(x, bounds.Y, _widths[index], height));
				x += _widths[index] + Gap;
			}

			if (_moreVisible)
			{
				if (_visible > 0) x += Gap;
				_more.Arrange(new Rect(x, bounds.Y, _more.DesiredSize.Width, height));
			}

			return;
		}

		var lineX = bounds.X;
		var lineY = bounds.Y;
		var lineWidth = 0.0;
		var lineHeight = 0.0;
		for (var index = 0; index < _chips.Count; index++)
		{
			var chip = _chips[index];
			ArrangeWrapped(chip, ref lineX, ref lineY, ref lineWidth, ref lineHeight, bounds);
		}

		ArrangeWrapped(_more, ref lineX, ref lineY, ref lineWidth, ref lineHeight, bounds);
	}

	private void ArrangeWrapped(UIElement child, ref double x, ref double y, ref double lineWidth, ref double lineHeight, Rect bounds)
	{
		var width = child.DesiredSize.Width;
		var height = Math.Max(_chipHeight, child.DesiredSize.Height);
		if (lineWidth > 0 && lineWidth + Gap + width > bounds.Width)
		{
			y += lineHeight + Gap;
			x = bounds.X;
			lineWidth = 0;
			lineHeight = 0;
		}

		if (lineWidth > 0) x += Gap;
		child.Arrange(new Rect(x, y, width, height));
		x += width;
		lineWidth += (lineWidth > 0 ? Gap : 0) + width;
		lineHeight = Math.Max(lineHeight, height);
	}

	private void MeasureChips(double availableHeight)
	{
		for (var index = 0; index < _chips.Count; index++)
		{
			var chip = _chips[index];
			if (!chip.IsVisible) continue;
			chip.Measure(new Size(double.PositiveInfinity, availableHeight));
			_widths[index] = chip.DesiredSize.Width;
			_chipHeight = Math.Max(_chipHeight, chip.DesiredSize.Height);
		}
	}

	private double MoreWidth(int remaining)
	{
		_moreLabel.Text(Strings.ChangedFilesMore(remaining));
		_more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		return _more.DesiredSize.Width;
	}

	private void SetMore(string text, GlyphKind glyph, bool visible)
	{
		if (!string.Equals(_moreLabel.Text, text, StringComparison.Ordinal))
		{
			_moreLabel.Text(text);
			// 文字变了宽度也变：装箱用的是这一轮量到的宽度，排布前把它对齐到最终文案。
			_more.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		}

		_moreGlyph.Kind = glyph;
		Show(_more, visible);
		_moreVisible = visible;
	}

	/// 只在显隐真的变化时写属性：每次赋值都会把测量标脏，卷动/改宽时白跑一轮布局。
	private static void Show(UIElement element, bool visible)
	{
		if (element.IsVisible != visible) element.IsVisible = visible;
	}

	/// 折叠态行高：chip、+N 按钮取高者，再兜底到设计高度。
	private double RowHeight() =>
		Math.Max(ChipHeight, _moreVisible ? Math.Max(_chipHeight, _more.DesiredSize.Height) : _chipHeight);

	private double UsedWidth(int visible)
	{
		var used = 0.0;
		for (var index = 0; index < visible; index++) used += _widths[index] + (index > 0 ? Gap : 0);
		if (_moreVisible) used += (_visible > 0 ? Gap : 0) + _more.DesiredSize.Width;
		return used;
	}

	private Size WrapSize(double available)
	{
		var lineWidth = 0.0;
		var lineHeight = 0.0;
		var height = 0.0;
		var maxWidth = 0.0;
		for (var index = 0; index < _chips.Count; index++)
		{
			var chip = _chips[index];
			Accumulate(chip.DesiredSize.Width, Math.Max(_chipHeight, chip.DesiredSize.Height));
		}

		Accumulate(_more.DesiredSize.Width, Math.Max(_chipHeight, _more.DesiredSize.Height));
		return new Size(Math.Min(available, Math.Max(maxWidth, lineWidth)), height + lineHeight);

		void Accumulate(double width, double itemHeight)
		{
			if (lineWidth > 0 && lineWidth + Gap + width > available)
			{
				height += lineHeight + Gap;
				maxWidth = Math.Max(maxWidth, lineWidth);
				lineWidth = 0;
				lineHeight = 0;
			}

			lineWidth += (lineWidth > 0 ? Gap : 0) + width;
			lineHeight = Math.Max(lineHeight, itemHeight);
		}
	}

	private void Toggle()
	{
		_expanded = !_expanded;
		InvalidateMeasure();
	}

	private static TextBlock Meta(string text, double opacity) => new TextBlock()
		.Text(text)
		.FontSize(AppTheme.FontSizeMicro)
		.Opacity(opacity)
		.VerticalAlignment(VerticalAlignment.Center);

	private static Button Chip(ChatFileChange file, string hint, Action<ChatFileChange>? openDiff)
	{
		var (icon, role) = FileTypeIcons.For(file.Path);
		var content = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(5)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				AppIcons.Icon(icon, role, 14),
				new TextBlock()
					.Text(file.FileName)
					.FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.Medium)
					.MaxWidth(ChipNameWidth)
					.TextWrapping(TextWrapping.NoWrap)
					.TextTrimming(TextTrimming.CharacterEllipsis)
					.VerticalAlignment(VerticalAlignment.Center));
		if (file.Count > 1)
		{
			// 同一文件本轮被改多次：次数跟在文件名后，避免误以为只有一次编辑。
			content.Children(Meta($"×{file.Count}", 0.6).Margin(new Thickness(2, 0, 0, 0)));
		}

		if (file.Added > 0 || file.Removed > 0)
		{
			var stat = new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
				.Margin(new Thickness(2, 0, 0, 0))
				.VerticalAlignment(VerticalAlignment.Center);
			if (file.Added > 0)
			{
				stat.Children(new TextBlock().Text($"+{file.Added}").FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.Medium).Role(AppStyles.RoleSuccess)
					.VerticalAlignment(VerticalAlignment.Center));
			}

			if (file.Removed > 0)
			{
				stat.Children(new TextBlock().Text($"−{file.Removed}").FontSize(AppTheme.FontSizeMicro)
					.FontWeight(FontWeight.Medium).Role(AppStyles.RoleError)
					.VerticalAlignment(VerticalAlignment.Center));
			}

			content.Children(stat);
		}

		var chip = new Button()
			.StyleName(AppStyles.ChangedFileChip)
			.Padding(new Thickness(6, 2, 7, 2))
			.MinHeight(ChipHeight)
			.HoverTip(hint)
			.Content(content);
		if (openDiff is not null) chip.OnClick(() => openDiff(file));
		return chip;
	}
}
