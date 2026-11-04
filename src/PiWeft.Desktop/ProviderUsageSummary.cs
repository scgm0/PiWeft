using System.Text;
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
	private readonly List<Action<ProviderUsageResult>> _rowUpdaters = [];
	private string? _rowsShape;
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
		_status.Text(result.Success ? _current().Provider : result.Disabled ? Strings.UsageDisabled : result.Error ?? Strings.UsageUnavailable);
		// 失败时保留上一次成功的时间：错误由状态行说明，时间列不假装刚刚更新过。
		if (!result.Success)
		{
			ClearRows();
			return;
		}
		_time.Text(ProviderUsageDisplay.RelativeTime(result.At));

		// 5 分钟一次的自动刷新大多只是数值变化。结构不变时只改 Text/进度条，
		// 不重建控件，避免反复创建文本布局、字体与原生渲染资源。
		var shape = ShapeOf(result);
		if (!string.Equals(shape, _rowsShape, StringComparison.Ordinal))
		{
			ClearRows();
			_rowsShape = shape;
			BuildRows(result);
			return;
		}

		foreach (var update in _rowUpdaters) update(result);
	}

	/// <summary>行结构指纹：只包含决定“有哪些控件、什么配色”的字段，不包含会变的数值。</summary>
	private static string ShapeOf(ProviderUsageResult result)
	{
		var builder = new StringBuilder(64);
		builder.Append(result.Kind?.ToString() ?? "-").Append('|');
		if (result.Balance is not null)
			builder.Append('b').Append((int)ProviderUsageDisplay.ToneOf(result)).Append('|');
		if (result.Periods is { } periods)
		{
			Append(periods.Rolling);
			Append(periods.Weekly);
			Append(periods.Monthly);
		}
		if (result.Credits is { } credits)
		{
			builder.Append("c|").Append((int)ProviderUsageDisplay.ToneOf(result)).Append('|');
			var windows = credits.Windows ?? [];
			foreach (var window in windows)
			{
				var percent = ProviderUsageDisplay.WindowPercent(window);
				builder.Append(window.Key)
					.Append(percent is not null ? 'b' : 'a')
					.Append((int)ProviderUsageDisplay.PercentTone(percent));
				if (percent is null)
					builder.Append(window.Remaining is not null ? '1' : '0');
				builder.Append('|');
			}
			if (windows.Count == 0)
			{
				var remaining = RemainingOf(credits);
				builder.Append('r').Append(remaining is not null ? '1' : '0')
					.Append('u').Append(credits.Used is not null ? '1' : '0').Append('|');
			}
		}
		if (result.Booster is { } booster)
		{
			builder.Append("bo|")
				.Append(booster.MonthlyUsed is not null ? '1' : '0')
				.Append(booster.UnlimitedMonthly ? '1' : '0')
				.Append(booster.MonthlyChargeLimit is not null ? '1' : '0').Append('|');
		}
		return builder.ToString();

		void Append(UsagePeriod? period)
		{
			var percent = period?.Percent;
			builder.Append(percent is not null ? 'b' : 'a')
				.Append((int)ProviderUsageDisplay.PercentTone(percent)).Append('|');
		}
	}

	private void BuildRows(ProviderUsageResult result)
	{
		switch (result.Kind)
		{
			case ProviderUsageKind.Balance:
				if (result.Balance is { } balance)
					AddAmountRow(Strings.UsageBalanceShort,
						current => ProviderUsageDisplay.Money(current.Balance!.Value, current.Currency),
						ProviderUsageDisplay.ToneOf(result), result);
				break;
			case ProviderUsageKind.Periods:
				if (result.Periods is { } periods)
				{
					AddPeriod(Strings.UsageWindowRolling, periods.Rolling, result);
					AddPeriod(Strings.UsageWindowWeekly, periods.Weekly, result);
					AddPeriod(Strings.UsageWindowMonthly, periods.Monthly, result);
				}
				break;
			case ProviderUsageKind.Credits:
				if (result.Credits is not { } credits) break;
				var windows = credits.Windows ?? [];
				for (var index = 0; index < windows.Count; index++)
				{
					var window = windows[index];
					var slot = index;
					var percent = ProviderUsageDisplay.WindowPercent(window);
					if (percent is { } value)
					{
						AddBarRow(ProviderUsageDisplay.WindowLabel(window.Key),
							current => WindowValue((current.Credits?.Windows ?? [])[slot]),
							ProviderUsageDisplay.PercentTone(value), result);
					}
					else if (window.Remaining is not null)
					{
						AddAmountRow(ProviderUsageDisplay.WindowLabel(window.Key),
							current => ProviderUsageDisplay.Amount((current.Credits?.Windows ?? [])[slot].Remaining ?? 0),
							ProviderUsageDisplay.Tone.Neutral, result);
					}
				}
				if (windows.Count == 0)
				{
					if (RemainingOf(credits) is not null)
						AddAmountRow(Strings.UsageCreditsRemaining,
							current => ProviderUsageDisplay.Amount(RemainingOf(current.Credits!) ?? 0),
							ProviderUsageDisplay.ToneOf(result), result);
					if (credits.Used is not null)
						AddAmountRow(Strings.UsageCreditsUsed,
							current => ProviderUsageDisplay.Amount(current.Credits!.Used!.Value),
							ProviderUsageDisplay.Tone.Neutral, result);
				}
				break;
		}
		if (result.Booster is { } booster) BuildBooster(booster, result);

		void AddPeriod(string label, UsagePeriod? period, ProviderUsageResult current)
		{
			if (period?.Percent is not { } percent) return;
			AddBarRow(label, result => PeriodValue(result, label), ProviderUsageDisplay.PercentTone(percent), current);
		}
	}

	/// <summary>周期行只按标签取对应周期，避免把滚动/周/月的顺序硬编码进更新回调。</summary>
	private static (double Percent, string? Trailing) PeriodValue(ProviderUsageResult result, string label)
	{
		var periods = result.Periods;
		var period = label == Strings.UsageWindowRolling ? periods?.Rolling
			: label == Strings.UsageWindowWeekly ? periods?.Weekly
			: periods?.Monthly;
		if (period?.Percent is not { } percent) return (0, null);
		var trailing = period.ResetsAt is { } resetsAt && resetsAt > DateTimeOffset.UtcNow
			? $"{Strings.UsageResetAt} {ResetLabel(resetsAt)}" : null;
		return (percent, trailing);
	}

	private static (double Percent, string? Trailing) WindowValue(UsageWindow window)
	{
		var percent = ProviderUsageDisplay.WindowPercent(window) ?? 0;
		var trailing = window.Remaining is { } remaining and > 0
			? $"{Strings.UsageRemaining} {ProviderUsageDisplay.Amount(remaining)}" : null;
		return (percent, trailing);
	}

	private static double? RemainingOf(UsageCredits credits) =>
		credits.Remaining ?? (credits.Total is { } total && credits.Used is { } used ? total - used : null);

	/// <summary>重置时间：当天只给时分，跨天补月日，避免「重置 03:00」看不出是哪天。</summary>
	private static string ResetLabel(DateTimeOffset resetsAt)
	{
		var local = resetsAt.ToLocalTime();
		return local.Date == DateTimeOffset.Now.Date ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
	}

	private void BuildBooster(UsageBooster booster, ProviderUsageResult result)
	{
		_rows.Children(new Border().Height(1).Margin(new Thickness(0, 2, 0, 0))
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle));
		AddAmountRow(Strings.UsageBoosterBalance,
			current => ProviderUsageDisplay.Money(current.Booster!.Balance, current.Booster.Currency),
			ProviderUsageDisplay.Tone.Ok, result);
		if (booster.MonthlyUsed is not null)
			AddAmountRow(Strings.UsageBoosterMonthlyUsed,
				current => ProviderUsageDisplay.Money(current.Booster!.MonthlyUsed!.Value, current.Booster.Currency),
				ProviderUsageDisplay.Tone.Neutral, result);
		if (booster.UnlimitedMonthly)
			AddAmountRow(Strings.UsageBoosterMonthlyLimit, _ => Strings.UsageBoosterUnlimited,
				ProviderUsageDisplay.Tone.Neutral, result);
		else if (booster.MonthlyChargeLimit is not null)
			AddAmountRow(Strings.UsageBoosterMonthlyLimit,
				current => ProviderUsageDisplay.Money(current.Booster!.MonthlyChargeLimit!.Value, current.Booster.Currency),
				ProviderUsageDisplay.Tone.Neutral, result);
	}

	/// <summary>一行「标签 + 圆角进度条 + 百分比 + 尾部小字」，档位色与阈值对齐 PiDeck。</summary>
	private void AddBarRow(
		string label,
		Func<ProviderUsageResult, (double Percent, string? Trailing)> valueOf,
		ProviderUsageDisplay.Tone tone,
		ProviderUsageResult current)
	{
		var fill = new Border().CornerRadius(3)
			.WithTheme((theme, border) => border.Background = ToneColor(theme, tone));
		var trackGrid = new Grid();
		var track = new Border().Height(6).CornerRadius(3).ClipToBounds(true)
			.WithTheme((theme, border) => border.Background = AppTheme.For(theme).HoverBackground)
			.Child(trackGrid.Children(fill.Column(0), new Border().Column(1)));
		var percentText = new TextBlock()
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro).FontWeight(FontWeight.Bold)
			.HorizontalAlignment(HorizontalAlignment.Right).Width(40)
			.WithTheme((theme, block) => block.Foreground = ToneColor(theme, tone));
		var trailingText = new TextBlock().FontSize(AppTheme.FontSizeMicro).SubtleText()
			.VerticalAlignment(VerticalAlignment.Center);
		var labelText = new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).MutedText()
			.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis)
			.VerticalAlignment(VerticalAlignment.Center).Width(64);
		_rows.Children(new Grid().Columns(GridLength.Auto, GridLength.Stars(1), GridLength.Auto, GridLength.Auto)
			.Spacing(8)
			.Children(labelText.Column(0), track.Column(1), percentText.Column(2), trailingText.Column(3)));

		void Update(ProviderUsageResult result)
		{
			var (percent, trailing) = valueOf(result);
			var clamped = Math.Clamp(percent, 0, 100);
			trackGrid.Columns(GridLength.Stars(Math.Max(0.001, clamped)), GridLength.Stars(Math.Max(0.001, 100 - clamped)));
			percentText.Text(ProviderUsageDisplay.Percent(clamped));
			trailingText.Text(trailing ?? string.Empty).IsVisible(trailing is not null);
		}

		_rowUpdaters.Add(Update);
		Update(current);
	}

	/// <summary>一行「灰标签 + 右对齐彩色数值」。</summary>
	private void AddAmountRow(
		string label,
		Func<ProviderUsageResult, string> valueOf,
		ProviderUsageDisplay.Tone tone,
		ProviderUsageResult current)
	{
		var value = new TextBlock().FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption)
			.FontWeight(FontWeight.Bold).HorizontalAlignment(HorizontalAlignment.Right)
			.WithTheme((theme, block) => block.Foreground = ToneColor(theme, tone));
		_rows.Children(new DockPanel().LastChildFill().Children(
			new TextBlock().Text(label).FontSize(AppTheme.FontSizeMicro).MutedText()
				.VerticalAlignment(VerticalAlignment.Center).DockLeft(),
			value.DockRight()));

		void Update(ProviderUsageResult result) => value.Text(valueOf(result));
		_rowUpdaters.Add(Update);
		Update(current);
	}

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
		var target = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
		// 面板打开时 Show() 会在每次会话重绘时被调用；同间隔重复设置 Interval 会
		// Reschedule 并重置倒计时，导致自动查询永远等不到 5 分钟。
		if (_timer.IsEnabled && _timer.Interval == target)
		{
			return;
		}
		_timer.Interval = target;
		_timer.Start();
	}

	private void ClearRows()
	{
		foreach (var child in _rows.Children.ToArray()) UiLifetime.Release(child);
		_rows.Clear();
		_rowUpdaters.Clear();
		_rowsShape = null;
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
