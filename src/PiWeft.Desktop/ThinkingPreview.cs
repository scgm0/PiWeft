using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

namespace PiWeft.Desktop;

/// <summary>
/// 折叠态单行预览的取行策略（与 PiDeck 的 thinkingSummary 同语义）：
/// 流式中读「最新一行」（tail -f 语义），定稿后读「第一个非空行」（摘要从头部读起）。
/// </summary>
internal static class ThinkingPreview
{
	/// <summary>首行摘要的扫描窗口：定稿只算一次，但历史消息的思考可能上百 KB，设上限而不是从头扫到底。</summary>
	private const int FirstLineScanLimit = 4096;

	/// <summary>首行摘要的字符上限：宽度裁剪交给 <see cref="MarqueeLine"/> 与省略号，这里只防止超长单行整段进 TextBlock。</summary>
	private const int FirstLineLimit = 72;

	/// <summary>
	/// 流式中的最新一行：先裁掉尾部空白，再回退到最后一个换行之后。
	/// 思考是追加式增长的，回扫距离只有一行的长度，比每帧从头扫一遍整篇正文便宜得多；
	/// 先裁尾也让「最新一行」必定非空（行首缩进留在开头，由跑马灯自己推出画面）。
	/// </summary>
	internal static string LatestLine(string thinking) => LatestLine(thinking, thinking.Length);

	/// <summary>
	/// 只把前 <paramref name="length"/> 个字符当作已经写好的内容取最新一行：打字机逐帧推进时，
	/// 预览要看的不是「模型已经发完的那一行」，而是「已经露出来的那几个字」。
	/// </summary>
	internal static string LatestLine(string thinking, int length)
	{
		var span = thinking.AsSpan(0, Math.Clamp(length, 0, thinking.Length)).TrimEnd();
		// 只需要行尾那一段：跑马灯最多露出 LiveLineLimit 个字，更早的一定为负偏移裁在画面外。
		// 换行也只在这段窗口里回找 —— 不这样封顶的话，模型写出一段不带换行的长句时
		// 每帧都要反向扫完整篇（实测 32K 字一轮扫描就足以掉帧），TextBlock 也会重排整行。
		var from = Math.Max(0, span.Length - LiveLineLimit);
		// 窗口起点不能落在代理对中间（半个 emoji 会画出 tofu）。
		if (from > 0 && char.IsLowSurrogate(span[from])) from++;
		var window = span[from..];
		var newline = window.LastIndexOfAny('\n', '\r');
		var line = newline < 0 ? window : window[(newline + 1)..];
		return line.TrimStart().ToString();
	}

	/// <summary>定稿后的第一个非空行；<paramref name="settled"/> 为 true 表示后面再长的思考也不会改变它。</summary>
	internal static string FirstLine(string thinking, out bool settled)
	{
		settled = false;
		// 只在有限窗口里找：这条路径在思考结束时会再算一次，窗口只是防止把整篇正文扫一遍。
		var scan = Math.Min(thinking.Length, FirstLineScanLimit);
		var remaining = thinking.AsSpan(0, scan);
		if (thinking.Length > FirstLineScanLimit) settled = true;
		while (remaining.Length > 0)
		{
			var newline = remaining.IndexOf('\n');
			var line = (newline < 0 ? remaining : remaining[..newline]).Trim();
			if (line.Length > 0)
			{
				settled = true;
				return line.Length <= FirstLineLimit ? line.ToString() : line[..FirstLineLimit].ToString() + "…";
			}
			if (newline < 0) break;
			remaining = remaining[(newline + 1)..];
		}
		return string.Empty;
	}

	/// <summary>跟尾预览保留的行尾长度：足够填满最宽的阅读列，又给每帧的文本排版封了顶。</summary>
	private const int LiveLineLimit = 600;
}

/// <summary>
/// 流式文本的打字机：把「一次到达一整块 delta」摊成「每帧多打几个字」。
/// 与 PiDeck 的 useSmoothStream 同一套节奏：积压越多推进越快（backlog / 3），每帧封顶 24 字；
/// 同时滞后不能拖远 —— 跑马灯与正文都要「跟住最新」，落下几百字就变成在看旧文本。
/// </summary>
/// <remarks>
/// 游标是「权威文本里的一个前缀长度」，不复制文本：流式快照每帧都是新字符串，
/// 复制一份可见前缀等于每帧拷一遍整篇正文。消费端自己按 <see cref="Shown"/> 取前缀。
/// </remarks>
internal sealed class Typewriter
{
	/// <summary>每帧推进量 = 积压 / 该值：小步细走，大段追快。</summary>
	private const int CatchUpDivisor = 3;

	/// <summary>单帧最多露出的字符数：防止一次大 delta 把整句同时打出来。</summary>
	private const int MaxStepPerFrame = 24;

	/// <summary>允许的最大滞后：超过就把游标推回尾部附近，保证画面写的始终是「现在」。</summary>
	private const int MaxLag = 240;

	/// <summary>接手存量内容的阈值：一上来就这么长（视图回收后重建的行）不逐字，单条 delta 远小于它。</summary>
	private const int RevealOnBirth = 240;

	private string _target = string.Empty;
	private int _shown;
	// 上一次真正多打了字的时刻（ms）：长文本的降频间隔拿它算。
	private long _lastStepAt;

	/// <summary>已经露出的字符数：消费端只能看到 target[..Shown]。</summary>
	internal int Shown => Math.Min(_shown, _target.Length);

	/// <summary>当前权威全文（消费端比较可见前缀是否仍然成立时要用它，别自己拷一份）。</summary>
	internal string Target => _target;

	/// <summary>还有没打到的字：没打完就不能停时钟，也不能算这一帧已经收尾。</summary>
	internal bool CaughtUp => _shown >= _target.Length;

	internal void Reset()
	{
		_target = string.Empty;
		_shown = 0;
	}

	/// <summary>
	/// 喂入最新的完整文本。<paramref name="animated"/> = false（没窗口驱动：自检/单测，或这一行已经是
	/// 历史行）时直接全露出：逐字只对「正在流的这一行」有意义，打开旧会话把几百 KB 打一遍是灾难。
	/// </summary>
	internal void Set(string text, bool animated)
	{
		if (ReferenceEquals(text, _target))
		{
			if (!animated) _shown = text.Length;
			return;
		}
		var fresh = _target.Length == 0 && _shown == 0;
		_target = text;
		if (!animated)
		{
			_shown = text.Length;
			return;
		}
		// 一上来就接手一大段存量（视图被回收/重建、滚动时重新实现的行）：整段直接显示。
		// 逐字的意义是「看着这句被写出来」，而这只对在本视图里从零开始的行成立；
		// 把已经存在的半篇旧文本重打一遍，读起来像是内容在倒退。
		if (fresh && text.Length > RevealOnBirth)
		{
			_shown = text.Length;
			return;
		}
		// 文本变短 = 内容被改写或换了新一轮思考：从第一个字重新打，而不是拿着旧游标切新文本。
		if (_shown > text.Length) _shown = 0;
	}

	/// <summary>
	/// 推进一帧：返回本帧是否真的多打了字（没新字就不用碰任何下游视图）。
	/// 长文本自动降频：可见前缀越长，Markdown 尾块重排越贵（实测 6K 字 0.25ms/帧、24K 字 0.72ms/帧），
	/// 每帧都重排会把整窗拖进持续高负载。8K 字以内逐帧推，更大文本降到 16ms / 33ms 一跳：
	/// 60fps 与 30fps 的逐字观感区分不出来，重排次数却减半到三分之一；
	/// 而 30fps × 每帧 24 字 = 720 字/秒，仍快过任何真实输出速率，不会因为降频而追不上。
	/// </summary>
	internal bool Advance()
	{
		var backlog = _target.Length - _shown;
		if (backlog <= 0) return false;
		var interval = StepIntervalMs;
		if (interval > 0)
		{
			var now = Environment.TickCount64;
			if (now - _lastStepAt < interval) return false;
			_lastStepAt = now;
		}
		// 落下太多就补跳一段：宁可少打几个字，也不能停在半句上重打。
		if (backlog > MaxLag) _shown = _target.Length - MaxLag;
		var step = Math.Min(MaxStepPerFrame, Math.Max(1, (_target.Length - _shown) / CatchUpDivisor));
		var next = Math.Min(_target.Length, _shown + step);
		// 不要把代理对劈开：半截 emoji 会画出 tofu。
		if (next < _target.Length && char.IsHighSurrogate(_target[next - 1])) next++;
		_shown = next;
		return true;
	}

	/// <summary>推进间隔按可见前缀长度分档（同 PiDeck 随内容变长拉慢 minDelay 的做派）。</summary>
	private int StepIntervalMs => _target.Length switch
	{
		> 64_000 => 33,
		> 8_000 => 16,
		_ => 0,
	};
}

/// <summary>
/// 逐字推进的帧泵。MewUI 的 AnimationClock 由平台渲染脉冲驱动、注册即每帧一次回调，
/// 宿主不需要再造一个计时器；时钟只在「这一行还有字要打」时挂着，打完/收起/滚出视口就停，
/// 因为持续渲染会整窗心跳，没字可打时一帧都不该多花。
/// </summary>
/// <remarks>
/// 时长只是一个「每帧都转」的载体（同 SmoothScroll 拿一天的时长当纯帧触发器）：这里只看回调，
/// 不读进度。没窗口（自检/单测）时不启动，由数据帧自己推进。
/// </remarks>
internal sealed class StreamPump(FrameworkElement owner, Action onFrame) : IDisposable
{
	private readonly AnimationClock _clock = new(TimeSpan.FromDays(1), static value => value);

	internal void Start()
	{
		if (!Application.IsRunning || _clock.IsRunning) return;
		_clock.TickCallback = Tick;
		_clock.Start();
	}

	internal void Stop() => _clock.Stop();

	public void Dispose()
	{
		_clock.Stop();
		_clock.TickCallback = null;
	}

	/// <summary>
	/// 这一行现在值不值得继续吃帧。三道闸，任一不过就该停：
	/// 1) 还挂在窗口上 —— AnimationClock 是进程级注册，只要还有时钟在跑，宿主就进入持续渲染；
	///    一个泄漏在已拆分行上的时钟等于把整个应用拖进满帧重绘（也会污染靠
	///    AnimationManager 手动推帧的测试）。
	/// 2) 祖先没有把它关掉（折叠、切走的标签页、行被回收）。
	/// 3) 它落在祖先视口里：MewUI 按窗口绝对坐标排布滚出的内容，Bounds 与视口不相交
	///    就是真的看不见，重画它纯属白烧。
	/// </summary>
	internal static bool NeedsFrames(Element node)
	{
		if (node.FindVisualRoot() is not Window) return false;
		var bounds = node.Bounds;
		if (bounds.Width <= 0 || bounds.Height <= 0) return false;
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent is UIElement { IsVisible: false }) return false;
			if (parent is ScrollViewer viewport && !Intersects(viewport.Bounds, bounds)) return false;
		}
		return true;
	}

	private static bool Intersects(Rect a, Rect b) =>
		a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

	/// <summary>
	/// 每一拍先过三道闸（见 <see cref="NeedsFrames"/>），不过就自己停下来：
	/// 下一次数据帧还会走 Start，所以不会漏掉真正的恢复。
	/// </summary>
	private void Tick(double _)
	{
		if (!NeedsFrames(owner))
		{
			Stop();
			return;
		}
		onFrame();
	}
}

/// <summary>
/// 单行预览的尾部跟随容器（流式跑马灯）：把最新一行的<b>行尾</b>贴到可用宽度右边缘，
/// 左侧溢出由裁剪切掉 —— 新字符不断把旧字符推出左边缘，读到的始终是刚写下的那几个字。
/// 结束（<see cref="FollowTail"/> = false）后交回普通排版：行首对齐，超出部分由 TextBlock 自己的
/// 尾部省略号截断，摘要回到从头部读起。
/// 跟尾期间另有一道「扫光」从行首掠到行尾（和 PiDeck 同构），用动态提示「这一行还在写」；
/// 结束即消失，动画与流式状态完全同步。
/// </summary>
/// <remarks>
/// MewUI 的 TextBlock 只会「行首对齐 + 行尾省略号」，横向 ScrollViewer 又会带来滚动条、
/// 滚轮与焦点手势；这里用一个只包一行文本的 Panel：按完整行宽排版，再用负 X 偏移把行尾推到
/// 右边缘。每帧只在文本变化时重测（MewUI 自己缓存版面），也不引入第二个视口。
/// 被包的元素必须是不换行的单行文本，否则「按完整宽度测量」会把它撑开。
/// </remarks>
internal sealed class MarqueeLine : Panel
{
	/// <summary>扫光周期：和 PiDeck 的 shimmer-sweep 同量级，太快会闪、太慢看不出在动。</summary>
	private static readonly TimeSpan SweepPeriod = TimeSpan.FromMilliseconds(2200);

	/// <summary>扫光带宽（DIP）：比窄行还宽时按行宽收缩，不然整行一次就被盖住。</summary>
	private const double SweepBand = 300;

	/// <summary>扫光峰值不透明度（0-255）：这道光只是「还在写」的提示，不能抢正文。</summary>
	private const byte SweepAlpha = 0x2E;

	private readonly FrameworkElement _line;
	private readonly AnimationClock _sweep = new(SweepPeriod, static value => value) { RepeatCount = -1 };
	private Color _sweepColor;
	private LinearGradientBrush? _sweepBrush;
	private double _sweepPhase = -1;
	private bool _followTail;
	// 上一次测量得到的完整行宽：arrange 只有这个值可用，不能靠 DesiredSize（那已经是被裁剪过的宽度）。
	private double _lineWidth;

	public MarqueeLine(FrameworkElement line)
	{
		_line = line;
		ClipToBounds = true;
		Add(line);
		// 时钟每帧只做两件事：刷新相位 + 请求重画本行。看不见就不要重画：折叠、切走的标签页、
		// 滚出组体视口、乃至整行被拆下来回收，都会让时钟留在 AnimationManager 里把宿主拖进
		// 持续渲染，所以每一拍都过一遍 NeedsFrames。文本还在变就会再次走到测量，那里补回启动。
		_sweep.TickCallback = progress =>
		{
			if (!StreamPump.NeedsFrames(this))
			{
				_sweep.Stop();
				return;
			}
			_sweepPhase = progress;
			InvalidateVisual();
		};
	}

	/// <summary>
	/// 流式中把行尾贴到右边缘（跑马灯）并开启扫光；false 时回到行首 + 省略号，动画立刻停。
	/// 只切换排布方式、不改文本，所以定稿那一帧只会重排一次。
	/// </summary>
	public bool FollowTail
	{
		get => _followTail;
		set
		{
			if (_followTail == value) return;
			_followTail = value;
			InvalidateMeasure();
			if (value)
			{
				StartSweep();
			}
			else
			{
				_sweep.Stop();
				// 相位回到「未开始」：结束那一帧就不会再画出一道残留的光带。
				_sweepPhase = -1;
				InvalidateVisual();
			}
		}
	}

	protected override Size MeasureContent(Size availableSize)
	{
		// 跟随态要按「完整一行」的真实宽度才有行尾可贴，所以一律给无限宽度测量：
		// NoWrap 的 TextBlock 本来就报整行宽度，尾部省略号是渲染期按 arrange 宽度才加的。
		_line.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
		_lineWidth = _line.DesiredSize.Width;
		// 从隐藏里回到画面时补回扫光：文本每帧都在变，测量也就每帧都走这里。
		if (_followTail) StartSweep();
		var width = double.IsFinite(availableSize.Width)
			? Math.Min(_lineWidth, Math.Max(0.0, availableSize.Width))
			: _lineWidth;
		return new Size(width, _line.DesiredSize.Height);
	}

	protected override void ArrangeContent(Rect bounds)
	{
		var width = Math.Max(0.0, bounds.Width);
		var height = Math.Max(0.0, bounds.Height);
		if (!_followTail)
		{
			// 行首对齐：文本只拿可用宽度，超出部分由 TextBlock 在渲染时截成省略号。
			_line.Arrange(new Rect(bounds.X, bounds.Y, width, height));
			return;
		}

		// 行尾贴右边缘：给一个不比可用宽度窄的 arrange，多出来的宽度留在左边被 ClipToBounds 切掉。
		var full = Math.Max(width, _lineWidth);
		_line.Arrange(new Rect(bounds.X + width - full, bounds.Y, full, height));
	}

	/// <summary>
	/// 扫光：Panel 的 OnRender 在子元素之前调用，所以这道半透明光带是**托在文字底下**掠过去的，
	/// 不会把字糊掉；OnRender 也不在 ClipToBounds 的裁剪范围内，必须自己剪一道。
	/// </summary>
	protected override void OnRender(IGraphicsContext context)
	{
		base.OnRender(context);
		if (!_followTail || _sweepPhase < 0) return;
		var bounds = Bounds;
		if (bounds.Width < 8 || bounds.Height <= 0) return;

		// 行程是「行宽 + 带宽」：光带整条从左边外进来、整条从右边外出去，不会有贴边突消失。
		var band = Math.Min(SweepBand, bounds.Width);
		var x = bounds.X - band + (bounds.Width + band) * _sweepPhase;
		context.Save();
		try
		{
			context.SetClip(bounds);
			context.FillRectangle(new Rect(x, bounds.Y, band, bounds.Height), SweepBrush());
		}
		finally
		{
			context.Restore();
		}
	}

	protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
	{
		base.OnThemeChanged(oldTheme, newTheme);
		InvalidateVisual();
	}

	protected override void OnDispose()
	{
		_sweep.Stop();
		_sweep.TickCallback = null;
		base.OnDispose();
	}

	private void StartSweep()
	{
		// 无窗口（自检/单测）没有渲染循环可驱动时钟，启动只会把时钟挂在进程里吃内存。
		if (!Application.IsRunning || _sweep.IsRunning) return;
		_sweep.Start();
	}

	/// <summary>光带颜色取思考语义色（与「正在思考」标签同源），两端透明、中间峰值。</summary>
	private LinearGradientBrush SweepBrush()
	{
		var color = AppTheme.For(Theme).Thinking;
		if (_sweepBrush is null || _sweepColor != color)
		{
			_sweepColor = color;
			_sweepBrush = new LinearGradientBrush(
				new Point(0, 0),
				new Point(1, 0),
				[
					new GradientStop(0.0, color.WithAlpha(0)),
					new GradientStop(0.5, color.WithAlpha(SweepAlpha)),
					new GradientStop(1.0, color.WithAlpha(0)),
				],
				SpreadMethod.Pad,
				GradientUnits.ObjectBoundingBox);
		}
		return _sweepBrush;
	}
}
