using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;

namespace PiWeft.Desktop;

/// <summary>
/// 会话统计面板底部的用量摘要：标题行（相对时间 + 刷新 + 配置）加动态结果区。
/// 查询只在面板打开时进行；关闭时取消请求并拒绝迟到结果，模型切换不会串号展示。
/// </summary>
internal sealed class ProviderUsageSummary
{
	private readonly Func<Model> _current;
	private readonly Func<string> _cwd;
	private readonly Func<Model, string, CancellationToken, Task<ProviderUsageResult>> _query;
	private readonly TextBlock _title = new();
	private readonly TextBlock _time = new();
	private readonly TextBlock _status = new();
	private readonly StackPanel _rows = new();
	private readonly Button _refresh;
	private readonly Button _configure;
	private readonly DispatcherTimer _timer;
	/// <summary>相对时间文本的分钟级刷新；查询本身由 <see cref="_timer"/> 按配置间隔驱动。</summary>
	private readonly DispatcherTimer _clock;
	private CancellationTokenSource? _abort;
	private (string Provider, string BaseUrl, string Cwd)? _key;
	private ProviderUsageResult? _result;
	private bool _open;
	private int _generation;

	public ProviderUsageSummary(Func<Model> current, Func<string> cwd, Action configure,
		Func<Model, string, CancellationToken, Task<ProviderUsageResult>>? query = null)
	{
		_current = current;
		_cwd = cwd;
		_query = query ?? ProviderUsageService.QueryProviderAsync;
		_refresh = AppIcons.Button("refresh-cw", Strings.UsageRefresh, () => _ = RefreshAsync());
		_configure = AppIcons.Button("settings", Strings.UsageConfigure, configure);
		_title.Text(Strings.UsageTitle).FontSize(AppTheme.FontSizeCaption).FontWeight(FontWeight.Bold).SubtleText();
		_time.FontSize(AppTheme.FontSizeMicro).SubtleText();
		_status.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap);
		_rows.Spacing(6);
		_timer = new DispatcherTimer(TimeSpan.FromMinutes(UsageProbeStore.DefaultIntervalMinutes));
		_timer.Tick += () =>
		{
			_timer.Stop();
			_ = RefreshAsync();
		};
		_clock = new DispatcherTimer(TimeSpan.FromMinutes(1));
		_clock.Tick += () =>
		{
			if (_open && _result is { } result && result.At != default)
				_time.Text(ProviderUsageDisplay.RelativeTime(result.At));
		};
		View = new Border().WithTheme((theme, border) => border.BorderBrush = AppTheme.For(theme).Border)
			.BorderThickness(new Thickness(0, 1, 0, 0))
			.Padding(new Thickness(0, AppTheme.Space2, 0, 0)).Child(new StackPanel().Spacing(6).Children(
				new DockPanel().LastChildFill().Children(_refresh.DockRight(), _configure.DockRight(),
					new StackPanel().Orientation(Orientation.Horizontal).Spacing(6).Children(_title, _time)),
				_status,
				_rows));
	}

	public Border View { get; }

	/// <summary>统计面板打开时调用：首次进入或结果过期就查询，复用缓存避免重复请求。</summary>
	public void Show()
	{
		var model = _current();
		var key = (model.Provider, model.BaseUrl, _cwd());
		if (_open && _key == key)
		{
			StartTimer();
			return;
		}
		_open = true;
		if (_key != key)
		{
			Cancel();
			_key = key;
			_result = null;
			_time.Text(string.Empty);
			ClearRows();
		}
		if (_result is null || DateTimeOffset.UtcNow - _result.At >= TimeSpan.FromMinutes(1)) _ = RefreshAsync();
		else { Render(_result); StartTimer(); }
	}

	public void Hide()
	{
		_open = false;
		Cancel();
	}

	/// <summary>配置或凭据变化：丢弃缓存，下次打开重新查询。</summary>
	public void Invalidate()
	{
		_result = null;
		_time.Text(string.Empty);
		ClearRows();
		_timer.Stop();
		_clock.Stop();
		// 面板还开着时（例如设置弹窗保存后配置变化）必须立刻重查；否则 Show 的缓存短路
		// 会让界面一直停在旧值，直到手动刷新或重新打开统计面板。
		if (_open) _ = RefreshAsync();
	}

	/// <summary>弹窗「测试」成功后把同一份结果热更到摘要，三处展示立刻一致。</summary>
	public void ApplyResult(ProviderUsageResult result)
	{
		if (!result.Success) return;
		var model = _current();
		_key = (model.Provider, model.BaseUrl, _cwd());
		_result = result;
		Render(result);
		StartTimer();
	}

	public async Task RefreshAsync()
	{
		if (!_open) return;
		Cancel();
		var model = _current();
		var cwd = _cwd();
		_key = (model.Provider, model.BaseUrl, cwd);
		var version = _generation;
		var cancellation = new CancellationTokenSource();
		_abort = cancellation;
		_refresh.IsEnabled = false;
		_status.Text(Strings.UsageLoading);
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		ProviderUsageResult result;
		try { result = await Task.Run(() => _query(model, cwd, cancellation.Token), cancellation.Token).ConfigureAwait(false); }
		catch (OperationCanceledException) { result = ProviderUsageResult.Failure(Strings.UsageTimeout); }
		catch (Exception ex) { result = ProviderUsageResult.Failure(ProviderUsageDisplay.ErrorText(ex)); }

		void Complete()
		{
			cancellation.Dispose();
			if (!_open || version != _generation) return;
			_abort = null;
			_refresh.IsEnabled = true;
			_result = result;
			Render(result);
			StartTimer();
		}
		if (dispatcher is null || dispatcher.IsOnUIThread) Complete();
		else dispatcher.BeginInvoke(Complete);
	}

	private void Render(ProviderUsageResult result)
	{
		ClearRows();
		_status.Text(result.Success ? _current().Provider : result.Disabled ? Strings.UsageDisabled : result.Error ?? Strings.UsageUnavailable);
		// 失败时保留上一次成功的时间：错误由状态行说明，时间列不假装刚刚更新过。
		if (!result.Success) return;
		_time.Text(ProviderUsageDisplay.RelativeTime(result.At));
		BuildRows(result);
	}

	private void BuildRows(ProviderUsageResult result)
	{
		switch (result.Kind)
		{
			case ProviderUsageKind.Balance:
				if (result.Balance is { } balance)
					_rows.Children(AmountRow(Strings.UsageBalanceShort,
						ProviderUsageDisplay.Money(balance, result.Currency), ProviderUsageDisplay.ToneOf(result)));
				break;
			case ProviderUsageKind.Periods:
				if (result.Periods is { } periods)
				{
					AddPeriod(Strings.UsageWindowRolling, periods.Rolling);
					AddPeriod(Strings.UsageWindowWeekly, periods.Weekly);
					AddPeriod(Strings.UsageWindowMonthly, periods.Monthly);
				}
				break;
			case ProviderUsageKind.Credits:
				if (result.Credits is not { } credits) break;
				var windows = credits.Windows ?? [];
				foreach (var window in windows)
				{
					var percent = ProviderUsageDisplay.WindowPercent(window);
					if (percent is { } value)
					{
						var trailing = window.Remaining is { } remaining and > 0
							? $"{Strings.UsageRemaining} {ProviderUsageDisplay.Amount(remaining)}" : null;
						_rows.Children(BarRow(ProviderUsageDisplay.WindowLabel(window.Key), value, trailing));
					}
					else if (window.Remaining is { } left)
						_rows.Children(AmountRow(ProviderUsageDisplay.WindowLabel(window.Key),
							ProviderUsageDisplay.Amount(left), ProviderUsageDisplay.Tone.Neutral));
				}
				if (windows.Count == 0)
				{
					var remaining = credits.Remaining ?? (credits.Total is { } total && credits.Used is { } used ? total - used : null);
					if (remaining is not null)
						_rows.Children(AmountRow(Strings.UsageCreditsRemaining,
							ProviderUsageDisplay.Amount(remaining.Value), ProviderUsageDisplay.ToneOf(result)));
					if (credits.Used is { } spent)
						_rows.Children(AmountRow(Strings.UsageCreditsUsed, ProviderUsageDisplay.Amount(spent),
							ProviderUsageDisplay.Tone.Neutral));
				}
				break;
		}
		if (result.Booster is { } booster) BuildBooster(booster);
		void AddPeriod(string label, UsagePeriod? period)
		{
			if (period?.Percent is not { } percent) return;
			var trailing = period.ResetsAt is { } resetsAt && resetsAt > DateTimeOffset.UtcNow
				? $"{Strings.UsageResetAt} {ResetLabel(resetsAt)}" : null;
			_rows.Children(BarRow(label, percent, trailing));
		}
	}

	/// <summary>重置时间：当天只给时分，跨天补月日，避免「重置 03:00」看不出是哪天。</summary>
	private static string ResetLabel(DateTimeOffset resetsAt)
	{
		var local = resetsAt.ToLocalTime();
		return local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
	}

	private void BuildBooster(UsageBooster booster)
	{
		_rows.Children(new Border().Height(1).Margin(new Thickness(0, 2, 0, 0))
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle));
		_rows.Children(AmountRow(Strings.UsageBoosterBalance,
			ProviderUsageDisplay.Money(booster.Balance, booster.Currency), ProviderUsageDisplay.Tone.Ok));
		if (booster.MonthlyUsed is { } monthlyUsed)
			_rows.Children(AmountRow(Strings.UsageBoosterMonthlyUsed,
				ProviderUsageDisplay.Money(monthlyUsed, booster.Currency), ProviderUsageDisplay.Tone.Neutral));
		if (booster.UnlimitedMonthly)
			_rows.Children(AmountRow(Strings.UsageBoosterMonthlyLimit, Strings.UsageBoosterUnlimited,
				ProviderUsageDisplay.Tone.Neutral));
		else if (booster.MonthlyChargeLimit is { } limit)
			_rows.Children(AmountRow(Strings.UsageBoosterMonthlyLimit,
				ProviderUsageDisplay.Money(limit, booster.Currency), ProviderUsageDisplay.Tone.Neutral));
	}

	/// <summary>一行「标签 + 圆角进度条 + 百分比 + 尾部小字」，档位色与阈值对齐 PiDeck。</summary>
	private static UIElement BarRow(string label, double percent, string? trailing)
	{
		var tone = ProviderUsageDisplay.PercentTone(percent);
		var clamped = Math.Clamp(percent, 0, 100);
		var fill = new Border().CornerRadius(3)
			.WithTheme((theme, border) => border.Background = ToneColor(theme, tone));
		var track = new Border().Height(6).CornerRadius(3).ClipToBounds(true)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).HoverBackground)
			.Child(new Grid()
				.Columns(GridLength.Stars(Math.Max(0.001, clamped)), GridLength.Stars(Math.Max(0.001, 100 - clamped)))
				.Children(fill.Column(0), new Border().Column(1)));
		var text = new TextBlock().Text(ProviderUsageDisplay.Percent(clamped))
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FontWeight(FontWeight.Bold)
			.HorizontalAlignment(HorizontalAlignment.Right).Width(40)
			.WithTheme((theme, block) => block.Foreground = ToneColor(theme, tone));
		var children = new List<UIElement>
		{
			new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).MutedText()
				.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
				.VerticalAlignment(VerticalAlignment.Center).Width(64),
			track.VerticalAlignment(VerticalAlignment.Center),
			text.VerticalAlignment(VerticalAlignment.Center),
		};
		if (trailing is not null)
			children.Add(new TextBlock().Text(trailing).FontSize(AppTheme.FontSizeMicro).SubtleText()
				.VerticalAlignment(VerticalAlignment.Center));
		return new Grid().Columns(GridLength.Auto, GridLength.Stars(1), GridLength.Auto, GridLength.Auto)
			.Spacing(8)
			.Children([.. children.Select((child, index) => child.Column(index))]);
	}

	/// <summary>一行「灰标签 + 右对齐彩色数值」。</summary>
	private static UIElement AmountRow(string label, string value, ProviderUsageDisplay.Tone tone) =>
		new DockPanel().LastChildFill().Children(
			new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).MutedText()
				.VerticalAlignment(VerticalAlignment.Center).DockLeft(),
			new TextBlock().Text(value).FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption)
				.FontWeight(FontWeight.Bold).HorizontalAlignment(HorizontalAlignment.Right)
				.WithTheme((theme, block) => block.Foreground = ToneColor(theme, tone)).DockRight());

	internal static Color ToneColor(Theme theme, ProviderUsageDisplay.Tone tone)
	{
		var palette = AppTheme.For(theme);
		return tone switch
		{
			ProviderUsageDisplay.Tone.Ok => palette.Success,
			ProviderUsageDisplay.Tone.Low => palette.Warning,
			ProviderUsageDisplay.Tone.Empty => palette.Error,
			_ => palette.TextSubtle,
		};
	}

	private void StartTimer()
	{
		if (!_open || !Application.IsRunning)
		{
			_timer.Stop();
			_clock.Stop();
			return;
		}
		if (_result is { Success: true }) _clock.Start();
		else _clock.Stop();
		var minutes = _result?.IntervalMinutes ?? 0;
		if (!_open || minutes <= 0 || !Application.IsRunning)
		{
			_timer.Stop();
			return;
		}
		_timer.Interval = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
		_timer.Start();
	}

	private void ClearRows()
	{
		foreach (var child in _rows.Children.ToArray()) UiLifetime.Release(child);
		_rows.Clear();
	}

	private void Cancel()
	{
		_generation++;
		_abort?.Cancel();
		_abort = null;
		_refresh.IsEnabled = true;
		_timer.Stop();
		_clock.Stop();
	}
}
