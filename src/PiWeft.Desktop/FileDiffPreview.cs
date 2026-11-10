using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 轮末文件 chip 的 diff 浮层：窗口级浮层，卡片居中，头部是文件名、完整路径、增删行数与操作。
/// 补丁取自本轮工具结果，不重新读工作区：轮末要确认的是「这一轮改了什么」，
/// 文件后来可能已被下一轮改过。
internal sealed class FileDiffPreview
{
	private const double MaxWidth = 1080;

	private static readonly Color ScrimColor = new(0x9a, 0x00, 0x00, 0x00);

	private readonly Grid _view;
	private readonly TextBlock _title = new();
	private readonly TextBlock _path = new();
	private readonly TextBlock _stat = new();
	private readonly Border _body = new();
	private readonly Button _unified;
	private readonly Button _sideBySide;
	private readonly Button _open;
	private readonly Button _close;
	private readonly TextFocusScrollGuard _focus;
	private readonly OverlayFade _fade;

	private Window? _window;
	private UIElement? _returnFocus;
	private ChatFileChange? _file;
	private string _fullPath = string.Empty;
	private Action<string>? _openInEditor;
	private bool _sideBySideMode;

	public FileDiffPreview()
	{
		_title.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.SemiBold).PrimaryText()
			.MaxWidth(260).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		_stat.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FaintText()
			.TextWrapping(TextWrapping.NoWrap).VerticalAlignment(VerticalAlignment.Center);
		_path.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).SubtleText()
			.MaxWidth(420).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);

		_unified = ModeButton(Strings.DiffUnified, () => SetMode(false));
		_sideBySide = ModeButton(Strings.DiffSideBySide, () => SetMode(true));
		var modes = new Border()
			.StyleName(AppStyles.SegmentTrack)
			.Padding(new Thickness(2))
			.VerticalAlignment(VerticalAlignment.Center)
			.Child(new StackPanel().Orientation(Orientation.Horizontal).Spacing(2).Children(_unified, _sideBySide));

		_open = AppIcons.Button("square-pen", Strings.OpenInEditor, OpenInEditor, size: 14);
		_close = AppIcons.Button("x", Strings.GitCloseDiff, Hide, size: 14);
		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(modes, _open, _close);

		var headerContent = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(_title, _stat, _path);
		var header = new Border().StyleName(AppStyles.DialogHeader)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.Child(new DockPanel().LastChildFill().Children(actions.DockRight(), headerContent));

		var scroll = new ScrollViewer()
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2))
			.SmoothScrolling()
			.AutoVerticalScroll()
			.AutoHorizontalScroll()
			.Content(_body);
		// 点击补丁正文会聚焦 MultiLineTextBox：框架的「聚焦元素滚入视野」会把整块正文
		// 当成一个元素滚到顶端，读者刚滚到的位置就丢了。这里用会话同一套守卫保存/撤回
		// 这一帧的视口位移。
		_focus = new TextFocusScrollGuard(scroll);

		var card = new Border().StyleName(AppStyles.Dialog).CornerRadius(AppTheme.RadiusLg)
			.Child(new DockPanel().LastChildFill().Children(header.DockTop(), scroll));

		_view = new Grid().IsVisible(false).Children(
			new Button()
				.Background(ScrimColor)
				.BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false)
				.OnClick(Hide),
			new ShadowDecorator()
				.BlurRadius(24).OffsetY(8).CornerRadius(AppTheme.RadiusLg)
				.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
					? new Color(0xB4, 0x00, 0x00, 0x00)
					: new Color(0x3C, 0x00, 0x00, 0x00))
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Child(new PreviewSurface().Content(card)))
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				Hide();
			});
		_fade = new OverlayFade((FrameworkElement)_view);
		SetMode(false);
	}

	internal UIElement View => _view;

	internal bool IsOpen => _view.IsVisible;

	/// 打开某文件的 diff。宿主面缺失（无窗口、无头测试）时静默返回。
	public void Show(UIElement owner, ChatFileChange file, string fullPath, Action<string> openInEditor)
	{
		if (!Application.IsRunning) return;
		if (owner.FindVisualRoot() is not Window window) return;
		if (_window is not null && !ReferenceEquals(_window, window))
		{
			_fade.Cancel();
			CloseNow();
		}

		Load(file, fullPath, openInEditor);
		_window = window;
		if (IsOpen)
		{
			if (_fade.IsClosing) _fade.Reopen();
		}
		else
		{
			TransientOverlay.Open(window, _view, Hide);
			_returnFocus = window.FocusManager.FocusedElement;
			_view.IsVisible = true;
			_fade.Open();
		}

		_close.Focus();
	}

	public void Hide()
	{
		if (!IsOpen || _fade.IsClosing) return;
		_fade.Close(CloseNow);
	}

	/// 无窗口时装载内容，供无头测试检查头部与 diff 文本。
	internal void LoadForTests(ChatFileChange file, string fullPath) => Load(file, fullPath, null);

	private void Load(ChatFileChange file, string fullPath, Action<string>? openInEditor)
	{
		_file = file;
		_fullPath = fullPath;
		_openInEditor = openInEditor;
		_title.Text(file.FileName);
		_title.HoverTip(fullPath);
		_path.Text(fullPath);
		_stat.Text(Strings.ChangedFilesDelta(file.Added, file.Removed));
		_stat.IsVisible = _stat.Text.Length > 0;
		SetMode(false);
	}

	private void CloseNow()
	{
		_view.IsVisible = false;
		if (_window is { } window) TransientOverlay.Remove(window, _view);
		_window = null;
		_file = null;
		_openInEditor = null;
		SetChild(null);
		_returnFocus?.Focus();
		_returnFocus = null;
	}

	private void SetMode(bool sideBySide)
	{
		_sideBySideMode = sideBySide;
		_unified.StyleName(sideBySide ? AppStyles.SegmentButton : AppStyles.SegmentButtonActive);
		_sideBySide.StyleName(sideBySide ? AppStyles.SegmentButtonActive : AppStyles.SegmentButton);
		RenderDiff();
	}

	private void RenderDiff()
	{
		var patch = _file?.Patch ?? string.Empty;
		UIElement content = patch.Length == 0
			? new TextBlock()
				.Text(Strings.GitNoDiff)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.Margin(new Thickness(AppTheme.Space3))
			: _sideBySideMode
				? DiffView.RenderSideBySide(patch, _focus)
				: DiffView.Render(patch, _focus);
		SetChild(content);
	}

	private void SetChild(UIElement? content)
	{
		if (ReferenceEquals(_body.Child, content)) return;
		if (_body.Child is { } previous)
		{
			_body.Child = null;
			UiLifetime.Release(previous);
		}

		_body.Child = content;
	}

	private void OpenInEditor()
	{
		if (_file is null) return;
		_openInEditor?.Invoke(_fullPath);
	}

	private static Button ModeButton(string text, Action onClick) => new Button()
		.StyleName(AppStyles.SegmentButton)
		.Padding(new Thickness(10, 2, 10, 2))
		.MinHeight(24)
		.Content(new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro)
			.VerticalAlignment(VerticalAlignment.Center))
		.OnClick(onClick);

	/// 卡片高度跟内容走，最大不超过窗口；宽度固定一档，diff 的两列才有位置。
	private sealed class PreviewSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(MaxWidth, Math.Max(0, availableSize.Width - AppTheme.Space6 * 2));
			var height = Math.Max(0, availableSize.Height - AppTheme.Space6 * 2);
			var measured = base.MeasureContent(new Size(width, height));
			return new Size(width, Math.Min(height, measured.Height));
		}
	}
}
