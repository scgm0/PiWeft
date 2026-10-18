using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 设置弹窗：与 PiDeck 一样是窗口级模态浮层，而不是主区里的一个标签页。
/// </summary>
/// <remarks>
/// 结构：压暗背景 + 居中浮起卡片。卡片头部固定（标题、未保存提示、保存、关闭），
/// 内部再分为左侧导航轨（系统设置 / 配置管理分段 + 分区列表）与右侧可滚动内容。
/// 弹窗自己不持有配置草稿：脏状态、保存与关闭确认都委托给 <see cref="SettingsView"/>。
/// </remarks>
internal sealed class SettingsDialog
{
	/// <summary>浮层尺寸：大屏时停在 1120×820，小窗口按可用空间收缩。</summary>
	private const double MaxDialogWidth = 1120;
	private const double MaxDialogHeight = 820;

	/// <summary>压暗层与标题栏补片共用的颜色；背景已做低分辨率柔化时 50% 仍然保留足够层次。</summary>
	private static readonly Color ScrimColor = new(0x80, 0x00, 0x00, 0x00);

	private readonly SettingsView _settings;
	private readonly Button _saveButton;
	private readonly TextBlock _dirtyLabel;

	/// <summary>最大化/全屏时补上标题栏的压暗层：主浮层从标题栏下方开始，否则顶部会留一条亮边。</summary>
	private readonly Border _titleBarDim;

	private Window? _window;
	private bool _closing;

	public SettingsDialog(SettingsView settings)
	{
		_settings = settings;
		_settings.StateChanged += UpdateHeader;

		_dirtyLabel = new TextBlock().Text(Strings.ConfigDirty)
			.FontSize(AppTheme.FontSizeMicro).AccentText()
			.VerticalAlignment(VerticalAlignment.Center).IsVisible(false);
		_titleBarDim = new Border()
			.Background(ScrimColor)
			.Height(AppTheme.TitleBarHeight)
			.HorizontalAlignment(HorizontalAlignment.Stretch)
			.VerticalAlignment(VerticalAlignment.Top)
			.IsHitTestVisible(false);
		_saveButton = AppIcons.PrimaryButton("check", Strings.ConfigSave, Save)
			.Padding(new Thickness(14, 5, 14, 5))
			.MinHeight(AppTheme.ControlHeightSm + 2)
			.VerticalAlignment(VerticalAlignment.Center).IsVisible(false);

		var close = AppIcons.Button("x", Strings.SettingsClose, () => _ = RequestCloseAsync(), size: 15);
		close.Width(AppTheme.ControlHeightSm + 2).Height(AppTheme.ControlHeightSm + 2).Padding(Thickness.Zero);

		var title = new TextBlock().Text(Strings.Settings)
			.FontSize(AppTheme.FontSizeTitle)
			.FontWeight(FontWeight.SemiBold)
			.PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);

		var actions = new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2).VerticalAlignment(VerticalAlignment.Center)
			.Children(_dirtyLabel, _saveButton, close);
		var header = new Border().StyleName(AppStyles.DialogHeader).Height(58)
			.Padding(new Thickness(AppTheme.Space4 + 4, 0, AppTheme.Space3, 0))
			.Child(new DockPanel().LastChildFill().Children(actions.DockRight(), title));

		var card = new Border().StyleName(AppStyles.Dialog).CornerRadius(AppTheme.RadiusLg)
			.Child(new DockPanel().LastChildFill().Children(header.DockTop(), _settings.View));

		// 背景压暗层负责捕获点击与滚轮：点空白处走与关闭键相同的「放弃修改？」确认。
		View = new Grid().IsVisible(false).Children(
			new Button()
				.Background(ScrimColor)
				.BorderThickness(0).CornerRadius(0)
				.Cursor(CursorType.Arrow).Focusable(false)
				.OnClick(() => _ = RequestCloseAsync()),
			new ShadowDecorator()
				.BlurRadius(24).OffsetY(8).CornerRadius(AppTheme.RadiusLg)
				.WithTheme((theme, shadow) => shadow.ShadowColor = theme.IsDark
					? new Color(0xB4, 0x00, 0x00, 0x00)
					: new Color(0x3C, 0x00, 0x00, 0x00))
				.HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center)
				.Child(new DialogSurface().Content(card)))
			.OnKeyDown(key =>
			{
				if (key.Key != Key.Escape) return;
				key.Handled = true;
				_ = RequestCloseAsync();
			});
	}

	public UIElement View { get; }

	public bool IsOpen => View.IsVisible;

	/// <summary>收起后触发；宿主据此把焦点还给内容区。</summary>
	public event Action? Closed;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (IsOpen)
		{
			// 已经打开：只把焦点带回弹窗，不重置正在编辑的配置草稿。
			UpdateHeader();
			_settings.FocusNavigation();
			return;
		}

		_window = window;
		View.IsVisible(true);
		TransientOverlay.Open(window, View, Hide);
		window.WindowStateChanged += OnWindowStateChanged;
		ApplyOverlayInset();
		UpdateTitleBarDim();
		_settings.Refresh();
		UpdateHeader();
		AppStyles.ApplyTextColors((Element)View);
		_settings.FocusNavigation();
	}

	/// <summary>立即收起（不做保存确认）；供窗口关闭或其他浮层接管时使用。</summary>
	public void Hide()
	{
		if (!IsOpen) return;
		View.IsVisible(false);
		if (_window is { } window)
		{
			window.WindowStateChanged -= OnWindowStateChanged;
			window.OverlayLayer.Remove(_titleBarDim);
			TransientOverlay.Remove(window, View);
		}
		_window = null;
		_closing = false;
		Closed?.Invoke();
	}

	/// <summary>关闭请求：配置有未保存修改时先确认，取消则留在弹窗里。</summary>
	public async Task<bool> RequestCloseAsync()
	{
		if (!IsOpen) return true;
		if (_closing) return false;
		_closing = true;
		try
		{
			if (!await _settings.CanCloseAsync()) return false;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
		finally
		{
			_closing = false;
		}

		Hide();
		return true;
	}

	/// <summary>
	/// 换肤后重解析浮层子树并保留草稿/焦点。
	/// </summary>
	/// <remarks>
	/// 窗口换肤只通知浮层根元素（OverlayLayer 不在主视觉树里），子控件仍停留在旧主题，
	/// 会出现“文字已变浅、卡片还是深色”的半换肤状态。摘下再挂回会把 OnVisualRootChanged
	/// 传给整棵子树，让命名样式按新主题重新解析；控件本身不重建，输入内容与滚动位置都保留。
	/// </remarks>
	public void ApplyTheme()
	{
		if (!IsOpen || _window is not { } window)
		{
			AppStyles.ApplyTextColors((Element)View);
			return;
		}

		var focused = window.FocusManager.FocusedElement;
		window.OverlayLayer.Remove(View);
		window.OverlayLayer.Add(View);
		AppStyles.ApplyTextColors((Element)View);
		if (focused is not null)
		{
			window.FocusManager.SetFocus(focused, resolveDefault: false);
		}
		else
		{
			_settings.FocusNavigation();
		}
	}

	private void OnWindowStateChanged(WindowState _)
	{
		ApplyOverlayInset();
		UpdateTitleBarDim();
	}

	/// <summary>最大化/全屏时把浮层可见区域贴到窗口四边（透明阴影边已不存在）。</summary>
	private void ApplyOverlayInset()
	{
		if (_window is not { } window || View is not FrameworkElement element) return;
		element.Margin = window is MainWindow main
			? MainWindow.OverlayInsetFor(main.WindowState)
			: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
	}

	/// <summary>
	/// 最大化/全屏时把标题栏也纳入模态背景：主浮层从标题栏下方开始，补片负责这一条，
	/// 避免窗口铺满屏幕后顶部留下一条亮带。补片不参与命中测试，页签与窗口按钮仍可点击。
	/// </summary>
	private void UpdateTitleBarDim()
	{
		if (_window is not { } window) return;
		if (window.WindowState is not (WindowState.Maximized or WindowState.FullScreen))
		{
			window.OverlayLayer.Remove(_titleBarDim);
			return;
		}

		var inset = window is MainWindow main
			? MainWindow.OverlayInsetFor(main.WindowState)
			: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		_titleBarDim
			.Margin(new Thickness(inset.Left, Math.Max(0, inset.Top - AppTheme.TitleBarHeight), inset.Right, 0))
			.CornerRadius(CornerRadius.Zero);
		if (!window.OverlayLayer.Contains(_titleBarDim)) window.OverlayLayer.Add(_titleBarDim);
	}

	private void Save()
	{
		_settings.SaveDraft();
		UpdateHeader();
	}

	private void UpdateHeader()
	{
		var configuration = _settings.IsConfigMode;
		var dirty = configuration && _settings.IsDirty;
		_saveButton.IsVisible(configuration);
		_saveButton.IsEnabled(dirty && _settings.CanSave);
		_dirtyLabel.IsVisible(dirty);
	}

	/// <summary>弹窗卡片按可用空间收缩，但保持固定比例感：大屏不铺满，小窗不被裁切。</summary>
	private sealed class DialogSurface : ContentControl
	{
		protected override Size MeasureContent(Size availableSize)
		{
			var width = Math.Min(MaxDialogWidth, Math.Max(0, availableSize.Width - AppTheme.Space6 * 2));
			var height = Math.Min(MaxDialogHeight, Math.Max(0, availableSize.Height - AppTheme.Space6 * 2));
			base.MeasureContent(new Size(width, height));
			return new Size(width, height);
		}
	}
}
