using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 时间线里的等待占位行：消息已经发出去、屏幕上还没有本回合的任何输出时，
/// 在「回复本该开始出现的位置」先落一行轻量的等待提示。
///
/// 为什么要有这一行：首轮等待时正文区只有用户自己的气泡，既看不出请求是否被受理，
/// 也没有一处可以盯。首 token 一到，真正的流式行就接在这行原来的位置上。
/// 它不属于会话内容：不进投影、不进模型上下文、不进大纲，只是这一帧的界面。
///
/// 节拍放在行里自驱动，而不是等宿主每帧来推：这一行只在等待期存在，而等待期恰恰是宿主
/// 最没有事件可处理的时候；宿主也就不用为它保存引用，不必担心行被虚拟化器回收后引用发旧。
/// 看不见就自己停（与 <see cref="StreamPump"/> 同一套闸）。
/// </summary>
internal sealed class PendingReplyView : StackPanel
{
	private readonly Func<double> _elapsed;
	private readonly TextBlock _title;
	private readonly TextBlock _pulse;
	private readonly TextBlock _elapsedText;
	private DispatcherTimer? _clock;
	private bool _disposed;

	public PendingReplyView(Func<double> elapsedProvider)
	{
		_elapsed = elapsedProvider;
		// 与助手回复同一侧、同一起手位：这一行将来就是被真回复顶掉的那一行。
		Orientation = Orientation.Horizontal;
		Spacing = 6;
		MaxWidth = AppTheme.MessageMaxWidth;
		HorizontalAlignment = HorizontalAlignment.Stretch;
		Margin = new Thickness(AppTheme.Space3, AppTheme.Space1, AppTheme.Space3, AppTheme.Space1);

		// 呼吸点占固定宽度：它在长与缩，宽度一变整行就左右抖，等待中的眼睛会跟着晃。
		_pulse = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro)
			.FontFamily(AppTheme.MonoFont)
			// 固定宽度按最宽的一帧（「···」）留够，点变多时标题不会被推着左右晃。
			.Width(24)
			.InfoText()
			.VerticalAlignment(VerticalAlignment.Center);
		_title = new TextBlock()
			.Text(Strings.PendingReplyTitle)
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.VerticalAlignment(VerticalAlignment.Center);
		_elapsedText = new TextBlock()
			.FontSize(AppTheme.FontSizeMicro)
			.FaintText()
			.FontFamily(AppTheme.MonoFont)
			.VerticalAlignment(VerticalAlignment.Center)
			.IsVisible(false);

		Add(AppIcons.Icon("loader-circle", AppStyles.RoleInfo, 13));
		Add(_pulse);
		Add(_title);
		Add(_elapsedText);
		this.HoverTip(Strings.PendingReplyHint);
		Update();
	}

	/// <summary>这一行现在说的话（供渲染与单测读取）。</summary>
	internal string TitleText => _title.Text ?? string.Empty;

	internal string PulseText => _pulse.Text ?? string.Empty;

	internal string ElapsedText => _elapsedText.Text ?? string.Empty;

	protected override Size MeasureContent(Size availableSize)
	{
		// 从隐藏里回到画面时补回心跳：文本每秒都在变，测量每都会走到这里，比挂在事件上可靠。
		Start();
		return base.MeasureContent(availableSize);
	}

	private void Start()
	{
		// 没有窗口（自检、单测）就没有 dispatcher，只按数据帧推进；释放后不再排心跳。
		if (!Application.IsRunning || _clock is not null || _disposed) return;
		_clock = new DispatcherTimer(TimeSpan.FromMilliseconds(RespondingStatusPolicy.PulseStepMs));
		_clock.OnTick(OnTick);
		_clock.Start();
	}

	private void OnTick()
	{
		if (!StreamPump.NeedsFrames(this))
		{
			// 折叠、切走的标签页、滚出视口、整行被回收：停下来，下次测量再起来。
			Stop();
			return;
		}

		Update();
	}

	private void Update()
	{
		var elapsed = _elapsed();
		// 只在真的变了才写 Text：每一次写入都会让这一行重新测量并触发整窗重绘，
		// 而呼吸点在一个 4 帧循环里有两帧的字形是相同的（「··」出现两次）。
		var frame = RespondingStatusPolicy.PulseAt(elapsed);
		if (_pulse.Text != frame) _pulse.Text(frame);

		// 头两秒不报数：那时候「0 秒 / 1 秒」是噪音，不是信息。
		var show = elapsed >= RespondingStatusPolicy.ElapsedFromMs;
		_elapsedText.IsVisible(show);
		if (show)
		{
			var text = Strings.RespondingElapsed(elapsed);
			if (_elapsedText.Text != text) _elapsedText.Text(text);
		}
	}

	private void Stop()
	{
		if (_clock is not { } clock) return;
		_clock = null;
		clock.Stop();
		clock.Dispose();
	}

	protected override void OnDispose()
	{
		_disposed = true;
		Stop();
		base.OnDispose();
	}
}
