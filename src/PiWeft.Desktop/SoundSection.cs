using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>
/// 设置 → 提示音：总开关、音量、仅失焦，以及「完成 / 出错 / 等待输入」三类事件各自的
/// 开关与音效（带试听）。与 PiDeck 的提示音设置同构，默认值也一致：
/// 完成与出错开、等待输入关，音量 0.6。
///
/// 改动即时写入 <c>desktop.json</c> 的 <c>soundAlert</c> 段，不设草稿：
/// 提示音是「马上能听出来对不对」的偏好，多一层保存只会让用户以为没生效。
/// </summary>
public sealed class SoundSection
{
	private readonly StackPanel _body = new();
	/// <summary>拖动音量滑块时合并写盘：滑动一次会触发几十次 ValueChanged，逐次读写文件是白费。</summary>
	private readonly DispatcherTimer _saveTimer = new(TimeSpan.FromMilliseconds(350));
	/// <summary>还没落盘的设置；null 表示没有待保存改动。</summary>
	private SoundAlertSettings? _pending;

	public SoundSection()
	{
		_body.Spacing(AppTheme.Space3);
		_saveTimer.Tick += () => SaveNow();
		Rebuild();
	}

	public UIElement View => _body;

	/// <summary>界面重建入口：读当前偏好，铺一张总开关卡 + 一张事件卡。</summary>
	private void Rebuild()
	{
		var settings = DesktopPreference.Load().SoundAlert;
		_body.Clear();
		_body.Children(GeneralGroup(settings), EventsGroup(settings), Hint(Strings.SoundAlertHint));
	}

	private Element GeneralGroup(SoundAlertSettings settings)
	{
		var enabled = new CheckBox().Content(Label(Strings.SoundAlertEnabled));
		enabled.IsChecked = settings.Enabled;
		enabled.OnCheckedChanged(_ => Update(current => current with { Enabled = enabled.IsChecked == true }));

		var onlyWhenUnfocused = new CheckBox().Content(Label(Strings.SoundAlertOnlyWhenUnfocused));
		onlyWhenUnfocused.IsChecked = settings.OnlyWhenUnfocused;
		onlyWhenUnfocused.OnCheckedChanged(_ => Update(current => current with { OnlyWhenUnfocused = onlyWhenUnfocused.IsChecked == true }));

		// 音量：0–100 的滑块，落盘时换算回 0–1（与 PiDeck 的 volume 语义一致）。
		var volumeValue = new TextBlock()
			.Text($"{Math.Round(SoundPlayer.ClampVolume(settings.Volume) * 100)}%")
			.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeCaption).PrimaryText()
			.VerticalAlignment(VerticalAlignment.Center);
		var volume = new Slider()
			.Range(0, 100)
			.Value(Math.Round(SoundPlayer.ClampVolume(settings.Volume) * 100))
			.Width(220)
			.VerticalAlignment(VerticalAlignment.Center);
		volume.ValueChanged += value =>
		{
			volumeValue.Text($"{Math.Round(value)}%");
			// 滑块只入内存，松手 350 ms 后写一次（不逐帧读写 desktop.json）。
			Update(current => current with { Volume = Math.Round(value) / 100 }, immediate: false);
		};

		return Group("bell", Strings.SoundAlertGroup, new StackPanel().Spacing(AppTheme.Space2).Children(
			enabled,
			onlyWhenUnfocused,
			new DockPanel().LastChildFill().Children(
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
					.Children(volume, volumeValue).DockRight(),
				FieldLabel(Strings.SoundAlertVolume))));
	}

	private Element EventsGroup(SoundAlertSettings settings)
	{
		var body = new StackPanel().Spacing(AppTheme.Space2);
		foreach (var kind in new[] { SoundAlertKind.Done, SoundAlertKind.Error, SoundAlertKind.Waiting })
		{
			body.Children(EventRow(settings, kind));
		}

		return Group("list-checks", Strings.SoundAlertEventsGroup, body);
	}

	/// <summary>
	/// 一行事件：左侧开关与说明，右侧音效下拉 + 试听。
	/// 事件开关关闭时下拉仍可改（先配好再打开），但试听按钮始终可用——试听本来就是为了选个音。
	/// </summary>
	private Element EventRow(SoundAlertSettings settings, SoundAlertKind kind)
	{
		var config = settings.For(kind);
		var presets = SoundAlertEventSettings.Presets(kind);
		var enabled = new CheckBox().Content(Label(Title(kind)));
		enabled.IsChecked = config.Enabled;
		enabled.OnCheckedChanged(_ =>
			Update(current => current.With(kind, current.For(kind) with { Enabled = enabled.IsChecked == true })));

		var picker = new ComboBox()
			.ItemsSource(ItemsView.Create([.. presets.Select(SoundAlertJson.Label)]))
			.Width(140);
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

		return new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
				.Children(preview, picker).DockRight(),
			enabled);
	}

	/// <summary>
	/// 改一条设置：开关/音效这类单次操作立即落盘；滑块这类连续输入合并到 350 ms 后写。
	/// 只动 <c>soundAlert</c> 段，其它偏好原样保留。
	/// </summary>
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

	/// <summary>把待保存的设置写回 desktop.json（没有待保存改动时什么也不做）。</summary>
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

	/// <summary>预设列表里找下标（IReadOnlyList 没有 IndexOf）。</summary>
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

	private static TextBlock Label(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeCaption).MutedText();

	private static TextBlock FieldLabel(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeCaption).SubtleText().VerticalAlignment(VerticalAlignment.Center);

	private static TextBlock Hint(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeMicro).SubtleText().TextWrapping(TextWrapping.Wrap);

	private static Element Group(string icon, string title, UIElement content) => new Border()
		.StyleName(AppStyles.Card)
		.CornerRadius(AppTheme.RadiusMd)
		.MaxWidth(960)
		.ClipToBounds(true)
		.Child(new DockPanel().LastChildFill().Children(
			new Border().StyleName(AppStyles.CardHeader)
				.Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space3))
				.Child(new StackPanel().Orientation(Orientation.Horizontal)
					.VerticalAlignment(VerticalAlignment.Center).Spacing(AppTheme.Space2)
					.Children(
						AppIcons.Icon(icon, AppStyles.RoleSubtle, 14),
						new TextBlock().Text(title).FontSize(AppTheme.FontSizeCaption)
							.FontWeight(FontWeight.SemiBold).PrimaryText()
							.VerticalAlignment(VerticalAlignment.Center)))
				.DockTop(),
			new Border().Padding(new Thickness(AppTheme.Space4, AppTheme.Space3, AppTheme.Space4, AppTheme.Space4))
				.Child(content)));
}
