using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// 设置 → 提示音：总开关、音量、仅失焦，以及「完成 / 出错 / 等待输入」三类事件各自的
/// 开关与音效（带试听）。与 PiDeck 的提示音设置同构，默认值也一致：
/// 完成与出错开、等待输入关，音量 0.6。
/// 改动即时写入 <c>desktop.json</c> 的 <c>soundAlert</c> 段，不设草稿：
/// 提示音是「马上能听出来对不对」的偏好，多一层保存只会让用户以为没生效。
public sealed class SoundSection
{
	private readonly StackPanel _body = new();
	private readonly DispatcherTimer _saveTimer = new(TimeSpan.FromMilliseconds(350));
	private SoundAlertSettings? _pending;

	public SoundSection()
	{
		_body.Spacing(AppTheme.Space3);
		_saveTimer.Tick += () => SaveNow();
		Rebuild();
	}

	public UIElement View => _body;

	private void Rebuild()
	{
		var settings = DesktopPreference.Load().SoundAlert;
		_body.Clear();
		_body.Children(GeneralGroup(settings), EventsGroup(settings));
	}

	private Element GeneralGroup(SoundAlertSettings settings)
	{
		var rows = new List<UIElement>();
		var enabled = SettingsUi.SwitchRow(
			Strings.SoundAlertEnabled, Strings.SoundAlertEnabledHint, settings.Enabled,
			value => Update(current => current with { Enabled = value }), out _);
		rows.Add(enabled);
		var unfocused = SettingsUi.SwitchRow(
			Strings.SoundAlertOnlyWhenUnfocused, Strings.SoundAlertOnlyWhenUnfocusedHint, settings.OnlyWhenUnfocused,
			value => Update(current => current with { OnlyWhenUnfocused = value }), out _);
		rows.Add(unfocused);

		// 音量：0–100 的滑块，落盘时换算回 0–1（与 PiDeck 的 volume 语义一致）。
		var volumeValue = new TextBlock()
			.Text($"{Math.Round(SoundPlayer.ClampVolume(settings.Volume) * 100)}%")
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center).MinWidth(36);
		var volume = new Slider()
			.Range(0, 100)
			.Value(Math.Round(SoundPlayer.ClampVolume(settings.Volume) * 100))
			.Width(200)
			.VerticalAlignment(VerticalAlignment.Center);
		volume.ValueChanged += value =>
		{
			volumeValue.Text($"{Math.Round(value)}%");
			// 滑块只入内存，松手 350 ms 后写一次（不逐帧读写 desktop.json）。
			Update(current => current with { Volume = Math.Round(value) / 100 }, immediate: false);
		};
		rows.Add(SettingsUi.Row(Strings.SoundAlertVolume, Strings.SoundAlertVolumeHint,
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(volume, volumeValue)));

		return SettingsUi.Group("bell", Strings.SoundAlertGroup, SettingsUi.Rows(rows),
			description: Strings.SoundAlertHint);
	}

	private Element EventsGroup(SoundAlertSettings settings)
	{
		var rows = new List<UIElement>();
		foreach (var kind in new[] { SoundAlertKind.Done, SoundAlertKind.Error, SoundAlertKind.Waiting })
		{
			rows.Add(EventRow(settings, kind));
		}

		return SettingsUi.Group("list-checks", Strings.SoundAlertEventsGroup, SettingsUi.Rows(rows));
	}

	/// 一行事件：左边开关、标题与说明，右边音效下拉 + 试听。
	/// 事件开关关闭时下拉仍可改（先配好再打开），试听按钮始终可用：试听本来就是为了选个音。
	private UIElement EventRow(SoundAlertSettings settings, SoundAlertKind kind)
	{
		var config = settings.For(kind);
		var presets = SoundAlertEventSettings.Presets(kind);
		var enabled = new ToggleSwitch().IsChecked(config.Enabled)
			.HorizontalAlignment(HorizontalAlignment.Left)
			.VerticalAlignment(VerticalAlignment.Center);
		Aprillz.MewUI.Automation.AutomationProperties.SetName(enabled, Title(kind));
		enabled.OnCheckedChanged(value =>
			Update(current => current.With(kind, current.For(kind) with { Enabled = value })));

		var picker = new ComboBox()
			.ItemsSource(ItemsView.Create([.. presets.Select(SoundAlertJson.Label)]))
			.Width(150);
		picker.SelectedIndex = Math.Max(0, IndexOf(presets, config.Preset));
		picker.SelectionChanged += _ =>
		{
			var index = picker.SelectedIndex;
			if (index < 0 || index >= presets.Count) return;
			Update(current => current.With(kind, current.For(kind) with { Preset = presets[index] }));
		};

		// 试听：按当前设置里的音量播一次，不改变任何状态。
		var preview = AppIcons.Button("play", Strings.SoundAlertPreview, () =>
		{
			var current = DesktopPreference.Load().SoundAlert;
			var index = picker.SelectedIndex;
			var preset = index >= 0 && index < presets.Count ? presets[index] : config.Preset;
			SoundPlayer.Play(preset, current.Volume);
		}).StyleName(AppStyles.GhostButton);

		var label = new StackPanel().Spacing(2).Children(
			new TextBlock().Text(Title(kind)).FontSize(AppTheme.FontSizeCaption).PrimaryText(),
			SettingsUi.Hint(EventHint(kind)));

		var controls = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(enabled, preview, picker);
		return new Grid().Columns("*,auto").Spacing(AppTheme.Space3)
			.Padding(new Thickness(0, AppTheme.Space2 + 2, 0, AppTheme.Space2 + 2))
			.Children(label.Column(0), controls.Column(1));
	}

	private static string EventHint(SoundAlertKind kind) => kind switch
	{
		SoundAlertKind.Done => Strings.SoundAlertDoneHint,
		SoundAlertKind.Error => Strings.SoundAlertErrorHint,
		_ => Strings.SoundAlertWaitingHint,
	};

	/// 改一条设置：开关/音效这类单次操作立即落盘；滑块这类连续输入合并到 350 ms 后写。
	/// 只动 <c>soundAlert</c> 段，其它偏好原样保留。
	private void Update(Func<SoundAlertSettings, SoundAlertSettings> change, bool immediate = true)
	{
		var current = _pending ?? DesktopPreference.Load().SoundAlert;
		_pending = change(current);
		if (immediate)
		{
			SaveNow();
			return;
		}

		_saveTimer.Stop();
		_saveTimer.Start();
	}

	private void SaveNow()
	{
		_saveTimer.Stop();
		if (_pending is not { } settings) return;
		_pending = null;
		try
		{
			(DesktopPreference.Load() with { SoundAlert = settings }).Save();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 设置写不进去只影响「下次启动还记不记得」，不该让设置页崩掉。
			AppLog.Failure(AppLog.For<SoundSection>(), "Save sound alert settings", ex);
		}
	}

	private static int IndexOf(IReadOnlyList<SoundAlertPreset> presets, SoundAlertPreset preset)
	{
		for (var index = 0; index < presets.Count; index++)
		{
			if (presets[index] == preset) return index;
		}

		return 0;
	}

	private static string Title(SoundAlertKind kind) => kind switch
	{
		SoundAlertKind.Done => Strings.SoundAlertDone,
		SoundAlertKind.Error => Strings.SoundAlertError,
		_ => Strings.SoundAlertWaiting,
	};
}
