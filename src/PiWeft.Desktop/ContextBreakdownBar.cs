using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace PiWeft.Desktop;

/// <summary>
/// 上下文占用条 + 三段图例（系统 / 工具 / 对话），对齐 PiDeck 的 ContextMeter 面板：
/// 一条按窗口比例画的分段进度条，下面三行「色块 + 名称 + ~token」。
///
/// 为什么不用 <see cref="ProgressBar"/>：它只能表达一个标量。上下文占用里真正值得看的
/// 是构成——同是 40%，系统提示词与工具 schema 吃满和对话本身长，处理方式完全不同。
/// </summary>
internal sealed class ContextBreakdownBar
{
	private const double BarHeight = 4;

	private readonly Border _frame;
	private readonly Grid _bar;
	private readonly Border _system = Segment(theme => AppTheme.For(theme).TextSubtle);
	private readonly Border _tools = Segment(theme => AppTheme.For(theme).Thinking);
	private readonly Border _conversation = Segment(theme => AppTheme.For(theme).Info);
	private readonly Border _rest = Segment(static _ => new Color(0, 0, 0, 0));
	private readonly StackPanel _legend = new() { Spacing = 1, IsVisible = false };
	private readonly LegendRow[] _rows;
	/// <summary>上一次画出的三段份额（0.1% 精度）：流式期间数据每帧变，但份额常常不动，不必重排。</summary>
	private (int System, int Tools, int Conversation, int Remainder)? _drawn;

	public ContextBreakdownBar()
	{
		// 圆角与裁剪交给外层：内部段是方角，拼起来才是一条连续的条。
		_frame = new Border()
			.CornerRadius(2)
			.ClipToBounds(true)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).HoverBackground)
			.Child(_bar = new Grid().Columns("*,*,*,*").Height(BarHeight)
				.Children(_system.Column(0), _tools.Column(1), _conversation.Column(2), _rest.Column(3)))
			.IsVisible(false);
		View = new StackPanel().Spacing(AppTheme.Space2).Children(_frame, _legend);

		_rows =
		[
			new LegendRow("系统", theme => AppTheme.For(theme).TextSubtle),
			new LegendRow("工具", theme => AppTheme.For(theme).Thinking),
			new LegendRow("对话", theme => AppTheme.For(theme).Info),
		];
		_legend.Children(_rows[0].View, _rows[1].View, _rows[2].View);
	}

	public UIElement View { get; }

	public void Update(SessionStatisticsDisplay display)
	{
		if (display.Breakdown is not { IsEmpty: false } breakdown)
		{
			// 没有可用数据（会话尚未跑过模型）时整块不占位，不显示一条误导性的空条。
			_frame.IsVisible(false);
			_legend.IsVisible(false);
			return;
		}

		var window = Math.Max(1, display.ContextWindow);
		// 三段按窗口取份额，余量留空：占用 21% 时条只画 21%，一眼看得出还剩多少。
		var system = Share(breakdown.SystemTokens, window);
		var tools = Share(breakdown.ToolsTokens, window);
		var conversation = Share(breakdown.ConversationTokens, window);
		var rest = Math.Max(0.01, 100 - system - tools - conversation);

		// 段宽用 Grid 的星号比例表达，随卡片宽度自动伸缩；0 的段给极小权重，
		// 保证总权重非零的同时它实际上不可见。
		// 只在份额真的变了时重设列：面板开着 + 流式时统计每帧都会更新，
		// 每次都动列会白白触发一轮 measure/arrange。
		var drawn = ((int)Math.Round(system * 10), (int)Math.Round(tools * 10),
			(int)Math.Round(conversation * 10), (int)Math.Round(rest * 10));
		if (_drawn != drawn)
		{
			_drawn = drawn;
			_bar.Columns(
				GridLength.Stars(system),
				GridLength.Stars(tools),
				GridLength.Stars(conversation),
				GridLength.Stars(rest));
		}
		_frame.IsVisible(true);
		_legend.IsVisible(true);

		_rows[0].Update(breakdown.SystemTokens);
		_rows[1].Update(breakdown.ToolsTokens);
		_rows[2].Update(breakdown.ConversationTokens);
	}

	private static double Share(long tokens, int window) => tokens <= 0 ? 0.0001 : 100.0 * tokens / window;

	private static Border Segment(Func<Theme, Color> color) =>
		new Border().WithTheme((theme, border) => border.Background = color(theme));

	/// <summary>图例的一行：色块 + 名称 + 右对齐的 ~token 数。</summary>
	private sealed class LegendRow
	{
		private readonly Border _swatch;
		private readonly TextBlock _value = new();
		private readonly TextBlock _label;

		public LegendRow(string label, Func<Theme, Color> color)
		{
			_swatch = new Border()
				.Width(8).Height(8).CornerRadius(2)
				.VerticalAlignment(VerticalAlignment.Center)
				.WithTheme((theme, border) => border.Background = color(theme));
			_label = new TextBlock().Text(label)
				.FontSize(AppTheme.FontSizeCaption)
				.MutedText()
				.VerticalAlignment(VerticalAlignment.Center)
				.Margin(new Thickness(AppTheme.Space2, 0, 0, 0));
			_value.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center);
			View = new DockPanel().LastChildFill()
				.Children(_swatch.DockLeft(), _value.DockRight(), _label);
		}

		public DockPanel View { get; }

		public void Update(long tokens)
		{
			View.IsVisible(tokens > 0);
			var text = tokens > 0 ? $"~{SessionStatisticsDisplay.Compact(tokens)}" : string.Empty;
			if (_value.Text != text) _value.Text(text);
		}
	}
}
