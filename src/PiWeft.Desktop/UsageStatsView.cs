using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Stats;

namespace PiWeft.Desktop;

/// <summary>
/// D-07 用量统计：跨会话聚合每日/模型/项目用量并画热力图。
/// 扫描按需触发、单遍读取、文件数与行长有上限，不在后台常驻线程。
/// </summary>
internal sealed class UsageStatsView
{
	private readonly Func<string> _sessionsRoot;
	private readonly Func<string, string, ModelCostRates?> _rates;
	private readonly StackPanel _content = new();
	private readonly TextBlock _summary = new();
	private readonly TextBlock _notice = new();
	private readonly Button _refresh;
	private CancellationTokenSource? _abort;
	private int _generation;
	private bool _open = true;

	public UsageStatsView(Func<string> sessionsRoot, Func<string, string, ModelCostRates?>? rates = null)
	{
		_sessionsRoot = sessionsRoot;
		_rates = rates ?? ((_, _) => null);
		_content.Spacing(AppTheme.Space3);
		_refresh = AppIcons.Button("refresh-cw", Strings.UsageStatsRefresh, () => _ = RefreshAsync());

		View = new Border()
			.StyleName(AppStyles.Window)
			.Child(new ScrollViewer().SmoothScrolling()
				.Content(new StackPanel()
					.Spacing(AppTheme.Space3)
					.Padding(new Thickness(AppTheme.Space6))
					.Children(
						new TextBlock().Text(Strings.UsageStatsTitle).FontSize(18).FontWeight(FontWeight.Bold).PrimaryText(),
						new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
							.Children(_refresh, _summary.FontSize(AppTheme.FontSizeCaption).SubtleText()),
						_notice.FontSize(AppTheme.FontSizeCaption).AccentText().IsVisible(false),
						_content))
				.AutoVerticalScroll()
				.NoHorizontalScroll());

		_ = RefreshAsync();
	}

	public UIElement View { get; }

	public void Close()
	{
		_open = false;
		_abort?.Cancel();
		_abort = null;
	}

	public async Task RefreshAsync()
	{
		if (!_open)
		{
			return;
		}

		var generation = ++_generation;
		_abort?.Cancel();
		var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		_abort = cancellation;
		_refresh.IsEnabled = false;
		_summary.Text(Strings.UsageStatsLoading);

		var root = _sessionsRoot();
		UsageStatsReport? report = null;
		string? error = null;
		try
		{
			report = await Task
				.Run(() => UsageStats.Scan(root, new UsageStatsOptions { Rates = _rates }, cancellation.Token), cancellation.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			error = Strings.UsageStatsTimeout;
		}
		catch (Exception ex)
		{
			error = ex.Message;
		}

		void Complete()
		{
			cancellation.Dispose();
			if (!_open || generation != _generation)
			{
				return;
			}

			_abort = null;
			_refresh.IsEnabled = true;
			if (report is null)
			{
				_summary.Text(string.Empty);
				_notice.Text(error ?? Strings.UsageStatsUnavailable).IsVisible(true);
				return;
			}

			_notice.IsVisible(false);
			Render(report);
		}

		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			Complete();
		}
		else
		{
			dispatcher.BeginInvoke(Complete);
		}
	}

	private void Render(UsageStatsReport report)
	{
		foreach (var child in _content.Children.ToArray())
		{
			UiLifetime.Release(child);
		}

		_content.Children(_summary);
		_summary.Text(Strings.UsageStatsSummary(report.Days.Count, report.Totals.Messages,
			FormatTokens(report.Totals.Tokens), report.Totals.Cost));

		if (report.Totals.Messages == 0 && report.Totals.Tokens == 0)
		{
			_content.Children(Card(Strings.UsageStatsTitle, new TextBlock()
				.Text(Strings.UsageStatsEmpty).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)));
			return;
		}

		_content.Children(Card(Strings.UsageStatsHeatmap, Heatmap(report)));
		_content.Children(Card(Strings.UsageStatsModels, ModelList(report)));
		_content.Children(Card(Strings.UsageStatsProjects, ProjectList(report)));
	}

	/// <summary>26 周 × 7 天的方块图；行是星期，列是周。</summary>
	private static Element Heatmap(UsageStatsReport report)
	{
		const int weeks = 26;
		var cells = UsageStats.Heatmap(report, weeks);
		var grid = new StackPanel().Spacing(2);
		for (var weekday = 0; weekday < 7; weekday++)
		{
			var row = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2);
			for (var week = 0; week < weeks; week++)
			{
				var cell = cells[(week * 7) + weekday];
				var square = new Border()
					.Width(12)
					.Height(12)
					.CornerRadius(2)
					.Background(LevelColor(cell.Level));
				square.ToolTip($"{cell.Date} · {FormatTokens(cell.Tokens)}");
				row.Children(square);
			}

			grid.Children(row);
		}

		return new StackPanel().Spacing(AppTheme.Space2).Children(
			grid,
			new TextBlock().Text(Strings.UsageStatsHeatmapHint).FontSize(AppTheme.FontSizeMicro).FaintText());
	}

	private static Color LevelColor(int level) => level switch
	{
		1 => new Color(0x9B, 0xE9, 0xB0),
		2 => new Color(0x4A, 0xD1, 0x7A),
		3 => new Color(0x2E, 0xA0, 0x52),
		4 => new Color(0x18, 0x6A, 0x36),
		_ => new Color(0xEB, 0xED, 0xF0),
	};

	private static Element ModelList(UsageStatsReport report)
	{
		var body = new StackPanel().Spacing(4);
		foreach (var model in report.Models.Take(20))
		{
			body.Children(new TextBlock()
				.Text($"{model.Provider} · {model.Model}：{FormatTokens(model.Totals.Tokens)} · {model.Totals.Messages} 条 · {model.Totals.Cost:0.####}")
				.FontSize(AppTheme.FontSizeCaption)
				.TextWrapping(TextWrapping.Wrap)
				.PrimaryText());
		}

		return body;
	}

	private static Element ProjectList(UsageStatsReport report)
	{
		var body = new StackPanel().Spacing(4);
		foreach (var project in report.Projects.Take(20))
		{
			body.Children(new TextBlock()
				.Text($"{project.Directory}：{FormatTokens(project.Totals.Tokens)} · {project.Totals.Cost:0.####}")
				.FontSize(AppTheme.FontSizeCaption)
				.TextWrapping(TextWrapping.Wrap)
				.PrimaryText());
		}

		return body;
	}

	private static Element Card(string title, Element body) => new Border()
		.StyleName(AppStyles.Card)
		.Padding(new Thickness(AppTheme.Space3))
		.Child(new StackPanel().Spacing(AppTheme.Space2).Children(
			new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Bold).SubtleText(),
			body));

	/// <summary>按量级缩写令牌数，避免统计行过长。</summary>
	internal static string FormatTokens(long tokens) => tokens switch
	{
		>= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.##}B",
		>= 1_000_000 => $"{tokens / 1_000_000.0:0.##}M",
		>= 1_000 => $"{tokens / 1_000.0:0.##}K",
		_ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
	};
}
