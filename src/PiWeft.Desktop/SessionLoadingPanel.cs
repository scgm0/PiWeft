using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 准备阶段会话面板里的加载段落：它不是浮层卡片，而是欢迎面板 StackPanel 里的一段，
/// 加载时把「开始一次会话」的文案换成进度（文件名 + 百分比 + 行数），随后「正在准备会话…」。
/// 没有中止按钮 —— 后台那一段只是在读文件，中断它没有恢复价值；
/// 出错时也不退回横幅，而是把错误留在同一段落里就地显示，直到用户关掉或开始下一次操作。
/// </summary>
internal sealed class SessionLoadingPanel
{
	private readonly StackPanel _view;
	private readonly TextBlock _title;
	private readonly TextBlock _detail;
	private readonly ProgressBar _bar;
	private readonly Button _dismiss;

	// 在途操作数：打开与改名可能叠在一起，最后一个结束才算结束。
	private int _active;
	private bool _failed;

	public SessionLoadingPanel()
	{
		_title = new TextBlock()
			.FontSize(20)
			.FontWeight(FontWeight.Medium)
			.PrimaryText()
			.TextAlignment(TextAlignment.Center)
			.TextWrapping(TextWrapping.Wrap)
			.HorizontalAlignment(HorizontalAlignment.Center);

		// 长度未知的文件先用不定进度，收到第一个字节进度后切成百分比。
		_bar = new ProgressBar()
			.Minimum(0).Maximum(100).Value(0)
			.IsIndeterminate(true)
			.Width(260).Height(4)
			.HorizontalAlignment(HorizontalAlignment.Center);

		_detail = new TextBlock()
			.FontSize(AppTheme.FontSizeCaption)
			.MutedText()
			.TextAlignment(TextAlignment.Center)
			.TextWrapping(TextWrapping.Wrap)
			.MaxWidth(420)
			.HorizontalAlignment(HorizontalAlignment.Center);

		_dismiss = new Button().Ghost()
			.Content(new TextBlock().Text(Strings.CloseTab).FontSize(AppTheme.FontSizeCaption).PrimaryText())
			.Height(28)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.IsVisible(false)
			.OnClick(Dismiss);

		_view = new StackPanel()
			.Spacing(AppTheme.Space2)
			.HorizontalAlignment(HorizontalAlignment.Center)
			.Children(_title, _bar, _detail, _dismiss);
	}

	/// <summary>挂在准备阶段会话面板里的段落。</summary>
	public StackPanel View => _view;

	/// <summary>状态变化后请宿主重算面板可见性（加载中要让准备阶段面板顶上去）。</summary>
	public Action? Changed { get; set; }

	/// <summary>是否正在加载或停在错误态。</summary>
	public bool Showing => _active > 0 || _failed;

	// 供测试与宿主读取的当前状态。
	public string TitleText => _title.Text ?? string.Empty;
	public string DetailText => _detail.Text ?? string.Empty;
	public bool ProgressVisible => _bar.IsVisible;
	public bool ProgressIndeterminate => _bar.IsIndeterminate;
	public double ProgressValue => _bar.Value;
	public bool DismissVisible => _dismiss.IsVisible;

	/// <summary>开始一项操作：段落进入进度态。</summary>
	public void Begin(string title)
	{
		_active++;
		_failed = false;
		_title.Text = title;
		_title.Foreground = AppTheme.TextPrimary;
		_detail.Text = string.Empty;
		_detail.Foreground = AppTheme.TextMuted;
		// 错误态不该留着空进度轨道，看起来像分割线；重新开始时再放回来。
		_bar.IsVisible(true);
		_bar.IsIndeterminate = true;
		_bar.Value = 0;
		_dismiss.IsVisible(false);
		Changed?.Invoke();
	}

	/// <summary>更新进度文案；<paramref name="fraction"/> 为空表示长度未知，继续用不定进度。</summary>
	public void Update(string detail, double? fraction)
	{
		if (_failed) return;
		_detail.Text = detail;
		if (fraction is { } value)
		{
			_bar.IsIndeterminate = false;
			_bar.Value = Math.Clamp(value * 100, 0, 100);
		}

		Changed?.Invoke();
	}

	/// <summary>操作结束：没有在途操作也没有错误时段落收起。</summary>
	public void Complete()
	{
		_active = Math.Max(0, _active - 1);
		if (_active == 0 && !_failed) Changed?.Invoke();
	}

	/// <summary>
	/// 操作失败：错误就地显示在准备阶段的位置上，
	/// 直到用户关掉它或开始下一次操作。
	/// </summary>
	public void Fail(string title, string detail)
	{
		_active = Math.Max(0, _active - 1);
		_failed = true;
		_title.Text = title;
		_title.Foreground = AppTheme.Error;
		_detail.Text = detail;
		_detail.Foreground = AppTheme.Error;
		_bar.IsVisible(false);
		_dismiss.IsVisible(true);
		Changed?.Invoke();
	}

	private void Dismiss()
	{
		_failed = false;
		Changed?.Invoke();
	}
}
