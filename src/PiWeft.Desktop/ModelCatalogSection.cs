using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Models;

namespace PiWeft.Desktop;

/// 设置 → 模型目录：展示内置/覆盖版本与备份，提供检查、更新与还原。
/// 读取与写入都走 <see cref="ModelCatalogOverlay"/> / <see cref="OnlineModelCatalog"/>，
/// 本类只负责状态展示与按钮时序；操作成功后通过回调让主窗口重建模型环境。
internal sealed class ModelCatalogSection
{
	private readonly string _agentDir;
	private readonly Action _changed;
	private readonly StackPanel _body = new();
	private readonly TextBlock _notice = new();
	private readonly List<Button> _buttons = [];
	private bool _busy;
	private string _noticeText = string.Empty;
	private bool _noticeError;

	public ModelCatalogSection(string agentDir, Action changed)
	{
		_agentDir = agentDir;
		_changed = changed;
		_body.Spacing(AppTheme.Space3);
		_notice.FontSize(AppTheme.FontSizeCaption).TextWrapping(TextWrapping.Wrap);
		View = Group("package", Strings.SettingsModelCatalog, _body);
		Rebuild();
	}

	public Element View { get; }

	private void Rebuild()
	{
		var status = ModelCatalogOverlay.Status(_agentDir);
		var preference = DesktopPreference.Load(_agentDir);
		_body.Clear();
		_buttons.Clear();

		_body.Children(Hint(Strings.ModelCatalogDescription));
		_body.Children(InfoRow(Strings.ModelCatalogSource, Strings.ModelCatalogSourceValue));
		_body.Children(InfoRow(Strings.ModelCatalogBuiltin,
			Strings.ModelCatalogInfo(status.Builtin.ProviderCount, status.Builtin.EntryCount)));
		var overlayText = status.Overlay is { } overlay
			? Strings.ModelCatalogOverlayInfo(overlay.Version, overlay.FetchedAt, overlay.ProviderCount, overlay.EntryCount)
			: status.OverlayInvalid ? Strings.ModelCatalogInvalid : Strings.ModelCatalogNone;
		_body.Children(InfoRow(Strings.ModelCatalogOverlay, overlayText, error: status.OverlayInvalid));
		_body.Children(InfoRow(Strings.ModelCatalogBackup,
			status.HasBackup ? Strings.ModelCatalogHasBackup : Strings.ModelCatalogNone));

		var autoUpdate = new CheckBox();
		autoUpdate.Content(new StackPanel().Spacing(2).Children(
			Label(Strings.ModelCatalogAutoUpdate).FontWeight(FontWeight.SemiBold),
			Hint(Strings.ModelCatalogAutoUpdateDesc)));
		autoUpdate.IsChecked(preference.ModelCatalogAutoUpdate);
		autoUpdate.OnCheckedChanged(_ =>
		{
			var enabled = autoUpdate.IsChecked == true;
			try
			{
				var current = DesktopPreference.Load(_agentDir);
				(current with
				{
					ModelCatalogAutoUpdate = enabled,
					// 刚打开开关时清掉上次检查时间：下次启动立即检查，不必等满 24 小时。
					ModelCatalogCheckedAt = enabled ? null : current.ModelCatalogCheckedAt,
				}).Save(_agentDir);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				Toasts.Error(View, Strings.ConfigFailed);
			}
		});
		_body.Children(autoUpdate);

		_body.Children(_notice.IsVisible(_noticeText.Length > 0));
		RefreshNotice();

		var check = ActionButton(Strings.ModelCatalogCheck, () => _ = CheckAsync());
		var update = AppIcons.PrimaryButton("download", Strings.ModelCatalogUpdate, () => _ = UpdateAsync());
		var restore = ActionButton(Strings.ModelCatalogRestore, () => _ = RestoreAsync(builtin: true));
		var previous = ActionButton(Strings.ModelCatalogRestorePrevious, () => _ = RestoreAsync(builtin: false));
		var open = ActionButton(Strings.ModelCatalogOpen, OpenFile);

		restore.IsEnabled(status.HasOverlayFiles);
		previous.IsEnabled(status.HasBackup);
		open.IsEnabled(status.Overlay is not null);
		_buttons.AddRange([check, update, restore, previous, open]);
		_body.Children(new StackPanel().Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2).Children([.. _buttons]));

		SetBusy(_busy);
	}

	private async Task CheckAsync()
	{
		if (_busy) return;
		SetBusy(true);
		SetNotice(Strings.ModelCatalogChecking, error: false);
		try
		{
			var result = await Task.Run(() => OnlineModelCatalog.CheckAsync(_agentDir)).ConfigureAwait(false);
			await ToUiThreadAsync().ConfigureAwait(true);
			if (!result.Ok)
			{
				SetNotice(FailureText(result.Error), error: true);
			}
			else if (result.HasUpdate)
			{
				SetNotice(Strings.ModelCatalogCheckAvailable(Describe(result.Remote), Describe(result.Local)), error: false);
			}
			else
			{
				SetNotice(Strings.ModelCatalogCheckLatest(Describe(result.Local)), error: false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetNotice(FailureText(ModelCatalogError.Network), error: true);
		}
		finally
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetBusy(false);
			Rebuild();
		}
	}

	private async Task UpdateAsync()
	{
		if (_busy) return;
		SetBusy(true);
		SetNotice(Strings.ModelCatalogUpdating, error: false);
		try
		{
			var result = await Task.Run(() => OnlineModelCatalog.UpdateAsync(_agentDir)).ConfigureAwait(false);
			await ToUiThreadAsync().ConfigureAwait(true);
			if (!result.Ok)
			{
				SetNotice(FailureText(result.Error), error: true);
			}
			else if (result.Updated)
			{
				SetNotice(Strings.ModelCatalogUpdated(result.Info?.Version, result.Info?.EntryCount ?? 0), error: false);
				_changed();
			}
			else
			{
				SetNotice(Strings.ModelCatalogCheckLatest(Describe(result.Info)), error: false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetNotice(FailureText(ModelCatalogError.None), error: true);
		}
		finally
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetBusy(false);
			Rebuild();
		}
	}

	private async Task RestoreAsync(bool builtin)
	{
		if (_busy) return;
		SetBusy(true);
		try
		{
			var result = await Task.Run(() => builtin
				? OnlineModelCatalog.RestoreBuiltin(_agentDir)
				: OnlineModelCatalog.RestorePrevious(_agentDir)).ConfigureAwait(false);
			await ToUiThreadAsync().ConfigureAwait(true);
			if (!result.Ok)
			{
				SetNotice(FailureText(result.Error, restore: true), error: true);
			}
			else
			{
				SetNotice(builtin ? Strings.ModelCatalogBannerRestored : Strings.ModelCatalogBannerRestoredPrevious, error: false);
				// 没有覆盖层时「还原内置版」只回报状态，不必重建整个模型环境。
				if (result.Updated) _changed();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetNotice(FailureText(ModelCatalogError.None), error: true);
		}
		finally
		{
			await ToUiThreadAsync().ConfigureAwait(true);
			SetBusy(false);
			Rebuild();
		}
	}

	private void OpenFile()
	{
		var path = ModelCatalogOverlay.EffectivePath(_agentDir);
		if (path is null)
		{
			SetNotice(Strings.ModelCatalogNone, error: true);
			return;
		}

		if (!Shell.OpenInEditor(path))
		{
			SetNotice(Strings.ConfigFailed, error: true);
		}
	}

	private void SetBusy(bool busy)
	{
		_busy = busy;
		if (!busy) return;
		foreach (var button in _buttons)
		{
			button.IsEnabled(false);
		}
	}

	private void SetNotice(string text, bool error)
	{
		_noticeText = text;
		_noticeError = error;
		RefreshNotice();
	}

	private void RefreshNotice()
	{
		_notice.Text(_noticeText).IsVisible(_noticeText.Length > 0);
		_notice.Role(_noticeError ? AppStyles.RoleError : AppStyles.RoleMuted);
	}

	private static string Describe(ModelCatalogInfo? info) => info is null
		? Strings.ModelCatalogNone
		: Strings.ModelCatalogOverlayInfo(info.Version, info.FetchedAt, info.ProviderCount, info.EntryCount);

	private static string FailureText(ModelCatalogError error, bool restore = false) => error switch
	{
		ModelCatalogError.Offline => Strings.ModelCatalogOffline,
		ModelCatalogError.Network => Strings.ModelCatalogFailNetwork,
		ModelCatalogError.Validation => restore ? Strings.ModelCatalogFailBackup : Strings.ModelCatalogFailValidation,
		ModelCatalogError.Write => Strings.ModelCatalogFailWrite,
		ModelCatalogError.NoBackup => Strings.ModelCatalogFailNoBackup,
		_ => Strings.ModelCatalogFailUnexpected,
	};

	private static Task ToUiThreadAsync()
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			return Task.CompletedTask;
		}

		var completion = new TaskCompletionSource();
		dispatcher.BeginInvoke(() => completion.TrySetResult());
		return completion.Task;
	}

	private static Button ActionButton(string text, Action action) =>
		new Button().Content(Label(text)).OnClick(action);

	private static TextBlock Label(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();

	private static TextBlock Hint(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);

	private static UIElement InfoRow(string label, string value, bool error = false)
	{
		var text = new TextBlock().Text(value).FontSize(AppTheme.FontSizeCaption)
			.TextWrapping(TextWrapping.Wrap).HoverTip(value);
		if (error) text.Role(AppStyles.RoleError); else text.PrimaryText();
		return new DockPanel().LastChildFill().Children(
			new TextBlock().Text(label).Width(116).FontSize(AppTheme.FontSizeCaption).SubtleText().DockLeft(),
			text);
	}

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
