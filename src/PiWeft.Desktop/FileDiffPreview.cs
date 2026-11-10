using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// 一份差异的浮层内容：标题（文件名）、来源上下文（如「提交 abc123」，可为空）、
/// 完整路径、增删行数、补丁，以及补丁不可用时的说明。
/// <param name="Message">替代 diff 显示的说明（读取失败 / 没有差异）；为空时按 <paramref name="Patch"/> 渲染。</param>
internal sealed record DiffPreviewContent(
	string Title,
	string Context,
	string Path,
	int Added,
	int Removed,
	string Patch,
	string? Message = null,
	bool IsError = false,
	bool CanOpenFile = true);

/// 文件差异浮层：窗口级浮层，卡片居中，头部是文件名、来源、完整路径、增删行数与操作。
/// 轮末 chip 用它展示本轮补丁（取自工具结果，不重读工作区）；
/// 右侧 Git 面板用它展示工作区 / 提交 / 分支比较里的单个文件差异。
internal sealed class FileDiffPreview
{
	private const double MaxWidth = 1080;

	private static readonly Color ScrimColor = new(0x9a, 0x00, 0x00, 0x00);

	private readonly Grid _view;
	private readonly TextBlock _title = new();
	private readonly TextBlock _context = new();
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
	private DiffPreviewContent? _content;
	private Action<string>? _openInEditor;
	private bool _sideBySideMode;

	public FileDiffPreview()
	{
		_title.FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.SemiBold).PrimaryText()
			.MaxWidth(260).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center);
		_context.FontSize(AppTheme.FontSizeMicro).SubtleText()
			.MaxWidth(160).TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
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
			.Children(_title, _context, _stat, _path);
		var header = new Border().StyleName(AppStyles.DialogHeader)
			.Padding(new Thickness(AppTheme.Space3, AppTheme.Space2, AppTheme.Space3, AppTheme.Space2))
			.Child(new DockPanel().LastChildFill().Children(actions.DockRight(), headerContent));

		var scroll = new ScrollViewer()
			.BorderThickness(0)
			.Background(AppTheme.Transparent)
			.Padding(new Thickness(AppTheme.Space2))
			.SmoothScrolling()
			.AutoVerticalScroll()
			// 不再横向滚动整块 body：并排的两列各自有横向视口，纵向视图给出无限宽会把
			// 两列的星号宽度压成内容宽，列内的滚动条就永远不出现（只剩这一条外层的）。
			// 统一视图的长行由它自己的横向视口负责。
			.NoHorizontalScroll()
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

	/// 「在编辑器中打开」是否可用：历史来源（提交 / 分支比较）不给这个入口。
	internal bool CanOpenFile => _open.IsVisible;

	/// 打开一份 diff。宿主面缺失（无窗口、无头测试）时静默返回。
	public void Show(UIElement owner, DiffPreviewContent content, Action<string>? openInEditor = null)
	{
		if (!Application.IsRunning) return;
		if (owner.FindVisualRoot() is not Window window) return;
		if (_window is not null && !ReferenceEquals(_window, window))
		{
			_fade.Cancel();
			CloseNow();
		}

		Load(content, openInEditor);
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

	/// 换肤：头部的角色文字重新上色，diff 主体按新配色重画（文本控件的颜色在创建时取）。
	public void ApplyTheme()
	{
		AppStyles.ApplyTextColors((Element)_view);
		// 关着的时候不必重建 diff：下次 Show 会按新配色重新装载。
		if (IsOpen) RenderDiff();
	}

	/// 无窗口时装载内容，供无头测试检查头部与 diff 文本。
	internal void LoadForTests(DiffPreviewContent content) => Load(content, null);

	private void Load(DiffPreviewContent content, Action<string>? openInEditor)
	{
		_content = content;
		_openInEditor = openInEditor;
		_title.Text(content.Title);
		_title.HoverTip(content.Path);
		_context.Text(content.Context);
		_context.IsVisible = content.Context.Length > 0;
		_path.Text(content.Path);
		_path.HoverTip(content.Path);
		_stat.Text(Strings.ChangedFilesDelta(content.Added, content.Removed));
		_stat.IsVisible = _stat.Text.Length > 0;
		// 历史来源（提交 / 分支比较）打开工作区文件会看错版本，这类请求不给编辑器入口。
		_open.IsVisible = content.CanOpenFile;
		SetMode(false);
	}

	private void CloseNow()
	{
		_view.IsVisible = false;
		if (_window is { } window) TransientOverlay.Remove(window, _view);
		_window = null;
		_content = null;
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
		var content = _content;
		UIElement body;
		if (content?.Message is { Length: > 0 } message)
		{
			var text = new TextBlock()
				.Text(message)
				.FontSize(AppTheme.FontSizeCaption)
				.TextWrapping(TextWrapping.Wrap)
				.Margin(new Thickness(AppTheme.Space3));
			if (content.IsError) text.ErrorText();
			else text.MutedText();
			body = text;
		}
		else
		{
			var patch = content?.Patch ?? string.Empty;
			body = patch.Length == 0
				? new TextBlock()
					.Text(Strings.GitNoDiff)
					.FontSize(AppTheme.FontSizeCaption)
					.MutedText()
					.Margin(new Thickness(AppTheme.Space3))
				: _sideBySideMode
					? DiffView.RenderSideBySide(patch, _focus)
					: DiffView.Render(patch, _focus);
		}

		SetChild(body);
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
		if (_content is not { CanOpenFile: true } content) return;
		_openInEditor?.Invoke(content.Path);
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
