using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 过程组组体内的步骤折叠行：展开后比组体视口更高的项，标题在本项范围内吸顶
/// （对齐 PiDeck 的 <c>processEntryStickyPolicy</c>：只对跨过视口顶边的展开项生效，
/// 本项末尾越过视口后标题随内容一起退场；不复制标题、不套第二层滚动条）。
/// </summary>
/// <remarks>
/// MewUI 没有 CSS 的 position:sticky，也没有可用的渲染变换，因此吸顶由本控件自己完成：
/// <list type="number">
/// <item>排布阶段把 Header 放到视口顶边（在本项范围内 clamp），正文留在原排布位置继续滚动；</item>
/// <item>绘制阶段先画正文（裁掉吸顶标题覆盖的那一段），再画标题与箭头 —— 正文不会盖住吸顶标题，
/// 标题背后也不需要不透明底色（裁掉的那段露出阅读区本身的背景，主题换肤不会失配）。
/// 覆盖 <see cref="RenderSubtree" /> 会让本行成为「兼容子树」，与流式 Markdown 现有的记录方式一致。</item>
/// <item>吸顶时标题右侧只多一个「回到这一步的开头」图标（滚回开头的同时解除组体跟底）。</item>
/// </list>
/// 只有挂到组体视口（<see cref="AttachBody" />）的步骤才吸顶：会话里的同一套视图保持原样。
/// </remarks>
internal sealed class StickyExpander : Expander
{
	/// <summary>Expander 内部留给折叠箭头的宽度（与框架里的常量一致）。</summary>
	private const double GlyphAreaWidth = 20;

	/// <summary>Expander 的标题最小高度（与框架里的常量一致）。</summary>
	private const double MinHeaderHeight = 20;

	/// <summary>亚像素容差：标题刚好贴住视口顶边时不要来回闪。</summary>
	private const double PinTolerance = 1;

	// 步骤行宿主（RowHost）用来把吸顶标题的底色/分隔线铺满整行（含左侧图标列）；
	// 组体视口提供「吸顶到哪」。
	private ScrollViewer? _body;
	private FrameworkElement? _rowHost;
	private Grid? _headerHost;
	private Button? _returnToStart;
	// 标题离开了自然位置（吸顶或随本项末尾上移）：绘制与点击判定都按位移后的位置来。
	private bool _displaced;
	// PiDeck 的 pinned：标题真的贴在组体视口顶边、且本项仍跨过顶边（本项末尾退场时不属于此列）。
	private bool _stuck;
	private bool _returnShown;
	// 本次 MouseDown 是否起于标题：Pointer 位置在窗口外取不到（无窗口的布局测试），
	// 因此用同一次事件对象来判断，而不是自己算坐标。
	private MouseEventArgs? _headerDown;
	// 吸顶标题的整条横带（含箭头区）：点击判定与分隔线都按它算。
	private Rect _headerBand;
	// 铺满整行的吸顶面（含左侧图标列），只画底部分隔线。
	private Rect _stickyFace;

	/// <summary>标题当前是否吸在组体视口顶部（显示「回到开头」入口、画分隔线的状态）。</summary>
	internal bool HeaderPinned => _stuck;

	/// <summary>标题是否已经离开自然位置：收起这一项时应把它的开头重新对齐到视口顶部。</summary>
	internal bool HeaderDisplaced => _displaced;

	/// <summary>标题当前所在的横带（含折叠箭头区）：吸顶时命中判定要按这里，而不是自然位置。</summary>
	internal Rect HeaderBand => _headerBand;

	/// <summary>
	/// 挂到组体：只有组体内的步骤才吸顶，并把「回到开头」入口加到标题行右侧。
	/// 入参在行绑定时传一次（视口/宿主随组体重建而更换）。
	/// </summary>
	internal void AttachBody(ScrollViewer body, FrameworkElement? rowHost)
	{
		if (ReferenceEquals(_body, body) && ReferenceEquals(_rowHost, rowHost)) return;
		DetachBody();
		_body = body;
		_rowHost = rowHost;
		// 窗口缩放会改变视口高度（吸顶资格随之变化），但本行自己的排布矩形可能没变，
		// 框架会跳过 Arrange；这里补一次失效，让吸顶判定按新视口重算。
		body.SizeChanged += OnBodyGeometryChanged;
		EnsureReturnToStart();
		UpdateReturnToStart();
		InvalidateArrange();
	}

	/// <summary>解除挂载（组体销毁/行被回收）：不留下对视图的事件订阅。</summary>
	internal void DetachBody()
	{
		if (_body is not null) _body.SizeChanged -= OnBodyGeometryChanged;
		_body = null;
		_rowHost = null;
		if (_displaced)
		{
			_displaced = false;
			_stuck = false;
			InvalidateVisual();
		}
		UpdateReturnToStart();
	}

	private void OnBodyGeometryChanged(SizeChangedEventArgs _) => InvalidateArrange();

	/// <summary>把「回到这一步的开头」图标放进标题行右侧；不吸顶时透明且不参与命中。</summary>
	private void EnsureReturnToStart()
	{
		if (_returnToStart is not null || _headerHost is not null || Header is not UIElement header) return;
		var button = new Button().Ghost()
			.Content(AppIcons.Icon("arrow-up-to-line", AppStyles.RoleSubtle, 12))
			.OnClick(ReturnToStart)
			.HoverTip(Strings.StepBackToStart)
			.Padding(new Thickness(2, 0, 2, 0)).MinWidth(18).MinHeight(18)
			.HorizontalAlignment(HorizontalAlignment.Right).VerticalAlignment(VerticalAlignment.Center)
			.Margin(new Thickness(4, 0, 0, 0));
		// 常驻占位、只切透明度：标题文字宽度不会因为图标出现而抖动，也不需要每次吸顶/退场重排标题。
		button.Opacity = 0;
		button.IsHitTestVisible = false;
		button.Focusable = false;
		// 先把原标题摘下来再包进两列 Grid：逻辑父子关系不能在同一帧里换两次。
		Header = null;
		_headerHost = new Grid().Columns("*,Auto").HorizontalAlignment(HorizontalAlignment.Stretch)
			.Children(header.Column(0), button.Column(1));
		_headerHost.MouseDown += OnHeaderMouseDown;
		Header = _headerHost;
		_returnToStart = button;
	}

	private void UpdateReturnToStart()
	{
		if (_returnToStart is not { } button || _returnShown == _stuck) return;
		_returnShown = _stuck;
		button.Opacity = _stuck ? 1 : 0;
		button.IsHitTestVisible = _stuck;
		button.Focusable = _stuck;
	}

	protected override void ArrangeContent(Rect bounds)
	{
		var rect = bounds.Deflate(Padding);
		var header = Header;
		var headerHeight = header is null ? MinHeaderHeight : Math.Max(header.DesiredSize.Height, MinHeaderHeight);
		var naturalY = rect.Y;
		var headerY = ResolveHeaderY(bounds, naturalY, headerHeight);
		// 吸顶是「标题比自然位置更靠下」：本项顶部已经滚出视口，标题被留在视口顶边。
		var displaced = headerY > naturalY + PinTolerance;
		if (!displaced) headerY = naturalY;
		// 末尾退场时标题会跟着本项上移（CSS sticky 同款）：那时不再算「吸住」，
		// 入口图标与分隔线都要收起来，否则会出现一个悬在正文上面的孤立标题。
		var stuck = displaced && _body is { } body && bounds.Bottom > body.Bounds.Y + headerHeight;
		if (displaced != _displaced || stuck != _stuck)
		{
			_displaced = displaced;
			_stuck = stuck;
			// 只有渲染结果变了（裁剪 + 箭头位置 + 分隔线 + 入口图标），布局不用重来。
			InvalidateVisual();
		}
		_headerBand = new Rect(rect.X, headerY, rect.Width, headerHeight);
		header?.Arrange(new Rect(rect.X + GlyphAreaWidth, headerY,
			Math.Max(0, rect.Width - GlyphAreaWidth), headerHeight));

		if (IsExpanded && Content is { } content)
		{
			var y = naturalY + headerHeight + Math.Max(0, HeaderSpacing);
			content.Arrange(new Rect(rect.X, y, rect.Width, Math.Max(0, rect.Bottom - y)));
		}

		// 吸顶面铺满整行：左侧图标列在吸顶期间已经滚出视口，留一条空白会看起来像缺了一角。
		var faceX = rect.X;
		var faceWidth = rect.Width;
		if (_rowHost is { Bounds.Width: > 0 } host)
		{
			faceX = host.Bounds.X;
			faceWidth = host.Bounds.Width;
		}
		_stickyFace = new Rect(faceX, headerY, faceWidth, headerHeight);
		UpdateReturnToStart();
	}

	/// <summary>
	/// 标题的目标位置：标题在原位就留在原位；跨过视口顶边则贴住顶边，离开本项时随本项末尾一起退场。
	/// </summary>
	private double ResolveHeaderY(Rect bounds, double naturalY, double headerHeight)
	{
		if (!IsStickyEligible(bounds.Height, headerHeight)) return naturalY;
		// 组体视口没有边框与内边距（ExecutionProcessView 直接 new ScrollViewer），视口顶边就是它自己的顶边。
		var viewportTop = _body!.Bounds.Y;
		// 1px 容差吸收高 DPI 下的亚像素偏差：刚贴住顶边时不要把图标/底色再闪一次。
		if (naturalY >= viewportTop - PinTolerance) return naturalY;
		return Math.Min(viewportTop, Math.Max(naturalY, bounds.Bottom - headerHeight));
	}

	/// <summary>只有「已展开 + 比组体视口更高」的项会跨过视口顶边，才需要吸顶。</summary>
	private bool IsStickyEligible(double entryHeight, double headerHeight)
	{
		if (!IsExpanded || headerHeight <= 0) return false;
		if (_body is not { } body) return false;
		if (!body.IsVisible || body.ViewportHeight <= 0) return false;
		return entryHeight > body.ViewportHeight + 0.5;
	}

	protected override void OnRender(IGraphicsContext context)
	{
		// 背景/描边仍按本行自身位置画；箭头改到 RenderSubtree 里画，才能落在吸顶位置且不被正文盖住。
		// （PiWeft 不给 Expander 套控件模板，因此不必像框架实现那样分流模板路径。）
		DrawBackgroundAndBorder(context, GetSnappedBorderBounds(Bounds), Background, BorderBrush, BorderThickness, CornerRadius);
	}

	protected override void RenderSubtree(IGraphicsContext context)
	{
		// 先画正文：吸顶标题要压在正文之上（框架默认顺序是标题→正文）。
		if (IsExpanded && Content is { } content) RenderContent(context, content);
		if (_stuck) DrawStickyFace(context);
		Header?.Render(context);
		DrawGlyph(context);
	}

	/// <summary>正文在吸顶标题下方继续滚动：把标题覆盖的那一段裁掉，标题就不需要不透明底色。</summary>
	private void RenderContent(IGraphicsContext context, Element content)
	{
		if (!_displaced || content.Bounds.Height <= 0)
		{
			content.Render(context);
			return;
		}
		var top = Math.Max(content.Bounds.Y, _headerBand.Bottom);
		var height = content.Bounds.Bottom - top;
		if (height <= 0.5)
		{
			// 正文已经整体落在标题后面：整段不画（它下面马上就是本项末尾）。
			return;
		}
		context.Save();
		context.SetClip(new Rect(content.Bounds.X, top, content.Bounds.Width, height));
		try { content.Render(context); }
		finally { context.Restore(); }
	}

	/// <summary>吸顶面只有一条底部分隔线：背面就是阅读区背景（正文已裁掉），换肤无需另配颜色。</summary>
	private void DrawStickyFace(IGraphicsContext context)
	{
		if (_stickyFace.Width <= 0 || _stickyFace.Height <= 0) return;
		var palette = AppTheme.For(Theme);
		context.FillRectangle(new Rect(_stickyFace.X, _stickyFace.Bottom - 1, _stickyFace.Width, 1),
			palette.BorderSubtle);
	}

	/// <summary>折叠箭头与框架默认绘制同源，只是跟着吸顶位置走。</summary>
	private void DrawGlyph(IGraphicsContext context)
	{
		if (_headerBand.Width <= 0 || _headerBand.Height <= 0) return;
		var center = new Point(_headerBand.X + GlyphAreaWidth / 2, _headerBand.Y + _headerBand.Height / 2);
		var color = IsEffectivelyEnabled ? Foreground : Theme.Palette.DisabledText;
		Glyph.Draw(context, center, GlyphSize, color, IsExpanded ? GlyphKind.ChevronDown : GlyphKind.ChevronRight);
	}

	/// <summary>标记这次按下发生在标题上（含吸顶后的位置）：吸顶时标题的开合判定只认它。</summary>
	private void OnHeaderMouseDown(MouseEventArgs e)
	{
		if (_displaced && !e.Handled && e.Button == MouseButton.Left && IsEffectivelyEnabled) _headerDown = e;
	}

	protected override void OnMouseDown(MouseEventArgs e)
	{
		// 吸顶后标题在视口顶部，而基类只认「标题在原位」的那个矩形：与吸顶横带重叠时，
		// 同一次点击会被算两次（重叠）或点「回到开头」图标也被当成开合。标题位移期间一律由这里判定。
		if (_displaced)
		{
			var fromHeader = ReferenceEquals(_headerDown, e);
			_headerDown = null;
			if (!fromHeader) return;
			CommitTargetValue(IsExpandedProperty, !IsExpanded);
			Focus();
			e.Handled = true;
			return;
		}
		_headerDown = null;
		base.OnMouseDown(e);
	}

	/// <summary>回到本项开头：滚回这一行的起点，同时解除组体的跟底（读者正在读历史步骤）。</summary>
	private void ReturnToStart()
	{
		if (_body is not { } body) return;
		var target = body.VerticalOffset + (Bounds.Y - body.Bounds.Y);
		if (SmoothScroll.Find(body) is { } smooth) smooth.RestoreReadingOffset(target);
		else body.SetScrollOffsets(body.HorizontalOffset, target);
	}

	protected override void OnDispose()
	{
		if (_headerHost is not null) _headerHost.MouseDown -= OnHeaderMouseDown;
		DetachBody();
		base.OnDispose();
	}
}
