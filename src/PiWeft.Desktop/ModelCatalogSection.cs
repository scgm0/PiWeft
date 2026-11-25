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
		View = SettingsUi.Group("package", Strings.ModelCatalogInfoGroup, _body,
			description: Strings.ModelCatalogDescription);
		Rebuild();
	}

	public Element View { get; }

	private void Rebuild()
	{
		var status = ModelCatalogOverlay.Status(_agentDir);
		var preference = DesktopPreference.Load(_agentDir);
		_body.Clear();
		_buttons.Clear();

		var infoRows = new List<UIElement>();
		infoRows.Add(SettingsUi.ValueRow(Strings.ModelCatalogSource, Strings.ModelCatalogSourceValue, mono: false));
		infoRows.Add(SettingsUi.ValueRow(Strings.ModelCatalogBuiltin,
			Strings.ModelCatalogInfo(status.Builtin.ProviderCount, status.Builtin.EntryCount), mono: false));
		var overlayText = status.Overlay is { } overlay
			? Strings.ModelCatalogOverlayInfo(overlay.Version, overlay.FetchedAt, overlay.ProviderCount, overlay.EntryCount)
			: status.OverlayInvalid ? Strings.ModelCatalogInvalid : Strings.ModelCatalogNone;
		infoRows.Add(SettingsUi.ValueRow(Strings.ModelCatalogOverlay, overlayText, mono: false,
			hint: status.OverlayInvalid ? overlayText : null));
		infoRows.Add(SettingsUi.ValueRow(Strings.ModelCatalogBackup,
			status.HasBackup ? Strings.ModelCatalogHasBackup : Strings.ModelCatalogNone, mono: false));

		var autoUpdate = SettingsUi.SwitchRow(
			Strings.ModelCatalogAutoUpdate, Strings.ModelCatalogAutoUpdateDesc, preference.ModelCatalogAutoUpdate,
			enabled =>
			{
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
			}, out _);
		infoRows.Add(autoUpdate);
		_body.Children(SettingsUi.Rows(infoRows));

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
		_body.Children(new Border().BorderThickness(new Thickness(0, 1, 0, 0))
			.Padding(new Thickness(0, AppTheme.Space3, 0, 0))
			.Child(new WrapPanel().Spacing(AppTheme.Space2).Children([.. _buttons]))
			.WithTheme(static (theme, border) => border.BorderBrush = AppTheme.For(theme).Border));

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
			await RunOnUiThreadAsync(() =>
			{
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
			}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await RunOnUiThreadAsync(() => SetNotice(FailureText(ModelCatalogError.Network), error: true)).ConfigureAwait(false);
		}
		finally
		{
			await RunOnUiThreadAsync(() =>
			{
				SetBusy(false);
				Rebuild();
			}).ConfigureAwait(false);
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
			await RunOnUiThreadAsync(() =>
			{
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
			}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await RunOnUiThreadAsync(() => SetNotice(FailureText(ModelCatalogError.None), error: true)).ConfigureAwait(false);
		}
		finally
		{
			await RunOnUiThreadAsync(() =>
			{
				SetBusy(false);
				Rebuild();
			}).ConfigureAwait(false);
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
			await RunOnUiThreadAsync(() =>
			{
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
			}).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			await RunOnUiThreadAsync(() => SetNotice(FailureText(ModelCatalogError.None), error: true)).ConfigureAwait(false);
		}
		finally
		{
			await RunOnUiThreadAsync(() =>
			{
				SetBusy(false);
				Rebuild();
			}).ConfigureAwait(false);
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

	/// 整段 UI 更新都要在回调里跑完。只等一个“已排到 UI 线程”的 Task 不够：
	/// await 之后的续行会用等待处的上下文，而 Task.Run 之后已经是线程池（没有同步上下文），
	/// 续行仍在线程池上改 `_body`。渲染线程同时在布局并枚举同一份子元素列表，
	/// 就会抛 Collection was modified。
	private static Task RunOnUiThreadAsync(Action action)
	{
		var dispatcher = Application.IsRunning ? Application.Current.Dispatcher : null;
		if (dispatcher is null || dispatcher.IsOnUIThread)
		{
			action();
			return Task.CompletedTask;
		}

		// continuation 不在 UI 线程内联执行：否则调用方的后续工作会撑在 dispatcher 回调里。
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		dispatcher.BeginInvoke(() =>
		{
			try
			{
				action();
				completion.TrySetResult();
			}
			catch (Exception ex)
			{
				completion.TrySetException(ex);
			}
		});
		return completion.Task;
	}

	private static Button ActionButton(string text, Action action) =>
		new Button().Content(Label(text)).OnClick(action);

	private static TextBlock Label(string text) => new TextBlock()
		.Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();

}
