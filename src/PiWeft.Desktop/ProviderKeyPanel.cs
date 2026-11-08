using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;

namespace PiWeft.Desktop;

/// API Key 凭据编辑面板（窗口级浮层）。
///
/// 为什么用表单，不照搬 pi 的逐步 prompt：桌面端同时要展示供应商、当前凭据状态、
/// 多字段（Cloudflare 账户/网关、Vertex 项目/区域、Bedrock AWS 密钥），一次看全比
/// 连续弹三次输入框更好核对。字段模型来自 <see cref="ProviderAuthCatalog"/>，与 CLI
/// 共用同一份「这个供应商要哪些字段」的事实。
internal sealed class ProviderKeyPanel
{
	private readonly ProviderAuthStatus _status;
	private readonly string _agentDir;
	private readonly Action<ProviderKeyPanel, bool> _saved;
	private readonly TextBlock _title = new();
	private readonly TextBlock _subtitle = new();
	private readonly TextBlock _current = new();
	private readonly TextBlock _warning = new();
	private readonly TextBlock _optionDescription = new();
	private readonly TextBlock _error = new();
	private readonly ComboBox _method = new();
	private readonly StackPanel _methodGroup = new();
	private readonly StackPanel _fields = new();
	private readonly StackPanel _fieldHint = new();
	private readonly Button _save;
	private readonly Grid _view;
	private readonly Dictionary<string, Func<string>> _readers = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Action<string>> _writers = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _initialEnvironment;
	private readonly string? _initialKey;
	private Window? _window;
	private readonly OverlayFade _fade;
	private ProviderKeyOption _selected;
	private bool _syncing;
	private bool _saving;
	private bool _confirming;
	private UIElement? _firstInput;

	public ProviderKeyPanel(ProviderAuthStatus status, string agentDir, Action<ProviderKeyPanel, bool> saved)
	{
		if (status.Key is null) throw new ArgumentException("provider has no API key auth", nameof(status));
		_status = status;
		_agentDir = agentDir;
		_saved = saved;
		_selected = ProviderKeySelection.Option(status.Key!, status.StoredKey);
		_initialEnvironment = status.StoredKey?.Environment is { Count: > 0 } environment
			? new Dictionary<string, string>(environment, StringComparer.Ordinal)
			: new Dictionary<string, string>(StringComparer.Ordinal);
		_initialKey = status.StoredKey?.HasKey == true ? status.StoredKey.Key : null;

		_title.Text(Strings.AuthKeyDialogTitleFor(status.Name)).FontSize(AppTheme.FontSizeTitle)
			.FontWeight(FontWeight.Bold).PrimaryText().TextTrimming(TextTrimming.CharacterEllipsis);
		_subtitle.Text(Strings.AuthKeyDialogSubtitle).FontSize(AppTheme.FontSizeMicro)
			.SubtleText().TextWrapping(TextWrapping.Wrap);
		_current.FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
			.MutedText().TextWrapping(TextWrapping.Wrap);
		_warning.Text(Strings.AuthKeyOverwriteOAuth).FontSize(AppTheme.FontSizeMicro).Role(AppStyles.RoleError)
			.TextWrapping(TextWrapping.Wrap).IsVisible(status.OAuthConfigured);
		_optionDescription.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);
		_error.FontSize(AppTheme.FontSizeCaption).Role(AppStyles.RoleError).TextWrapping(TextWrapping.Wrap);

		_method.ItemsSource(ItemsView.Create(status.Key.Options.Select(static option => option.Label).ToArray()))
			.SelectedIndex(OptionIndex(status.Key, _selected)).MinWidth(260);
		_method.SelectionChanged += _ =>
		{
			if (_syncing) return;
			var index = _method.SelectedIndex;
			if (index >= 0 && index < _status.Key.Options.Count) SelectOption(_status.Key.Options[index]);
		};
		_methodGroup.Spacing(4).Children(Label(Strings.AuthKeyMethodLabel), _method);
		_methodGroup.IsVisible(status.Key.Options.Count > 1);

		_save = AppIcons.PrimaryButton("check", Strings.AuthKeySave, Save);
		var clear = AppIcons.TextButton("trash", Strings.AuthKeyClear, () => _ = ClearAsync());
		clear.IsVisible(status.StoredKey is not null);

		var footer = new Grid().Columns("Auto,*,Auto").Children(
			clear.Column(0),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
				.HorizontalAlignment(HorizontalAlignment.Right).Children(
					AppIcons.TextButton("x", Strings.Cancel, Hide),
					_save).Column(2));

		var body = new ScrollViewer().SmoothScrolling().AutoVerticalScroll().NoHorizontalScroll().MaxHeight(560)
			.Content(new StackPanel().Spacing(AppTheme.Space3).Children(
				_current,
				_warning,
				_methodGroup,
				_optionDescription,
				_fields,
				_fieldHint,
				_error));

		var card = new Border().StyleName(AppStyles.Card).CornerRadius(AppTheme.RadiusMd)
			.Padding(new Thickness(AppTheme.Space3)).Child(new DockPanel().LastChildFill()
				.Spacing(AppTheme.Space2).Children(
					new StackPanel().Spacing(4).Children(
						new Grid().Columns("*,Auto").Children(
							_title.Column(0),
							AppIcons.Button("x", Strings.AuthClose, Hide).Column(1)),
						_subtitle).DockTop(),
					new Border().Height(1).DockTop()
						.WithTheme((theme, border) => border.Background = AppTheme.For(theme).BorderSubtle),
					footer.DockBottom(),
					body));

		_view = new Grid().IsVisible(false).Focusable(true).Children(
			new Button().Background(new Color(0x60, 0x00, 0x00, 0x00)).Cursor(CursorType.Arrow).Focusable(false)
				.BorderThickness(0).CornerRadius(0).OnClick(Hide),
			new AuthDialogSurface().MaxWidth(640).HorizontalAlignment(HorizontalAlignment.Center)
				.VerticalAlignment(VerticalAlignment.Center).Margin(new Thickness(AppTheme.Space3)).Content(card))
			.OnKeyDown(key =>
			{
				if (key.Key == Key.Escape)
				{
					key.Handled = true;
					Hide();
				}
				else if (key.Key == Key.Enter)
				{
					key.Handled = true;
					Save();
				}
			});

		BuildFields();
		_fade = new OverlayFade(_view);
	}

	public UIElement View => _view;

	public void Show(UIElement owner)
	{
		if (owner.FindVisualRoot() is not Window window) return;
		if (_view.IsVisible) { _fade.Cancel(); CloseNow(); }
		_window = window;
		if (View is FrameworkElement element)
		{
			element.Margin = window is MainWindow main
				? MainWindow.OverlayInsetFor(main.WindowState)
				: new Thickness(0, AppTheme.TitleBarHeight, 0, 0);
		}

		window.OverlayLayer.Add(View);
		View.IsVisible(true);
		_fade.Open();
		AppStyles.ApplyTextColors(View);
		_firstInput?.Focus();
		if (_firstInput is null) View.Focus();
	}

	private static int OptionIndex(ProviderKeyAuth key, ProviderKeyOption option)
	{
		for (var index = 0; index < key.Options.Count; index++)
		{
			if (ReferenceEquals(key.Options[index], option)) return index;
		}
		return 0;
	}

	private void SelectOption(ProviderKeyOption option)
	{
		_selected = option;
		BuildFields();
		if (_view.IsVisible) _firstInput?.Focus();
	}

	private void BuildFields()
	{
		_fields.Clear();
		_fieldHint.Clear();
		_readers.Clear();
		_writers.Clear();
		_firstInput = null;
		_error.Text(string.Empty);
		_optionDescription.Text(_selected.Description ?? string.Empty);
		_optionDescription.IsVisible(_selected.Description is { Length: > 0 });

		if (_selected.Fields.Count == 0)
		{
			_fieldHint.Children(Hint(_selected.Ambient
				? Strings.AuthKeyEnvironmentOnly
				: Strings.AuthKeyNoChanges));
		}

		foreach (var field in _selected.Fields)
		{
			var value = field.IsMainKey
				? string.Empty
				: _initialEnvironment.GetValueOrDefault(field.Name) ?? string.Empty;
			UIElement input;
			if (field.Secret)
			{
				var password = new PasswordBox().MinWidth(260);
				password.Password = value;
				_readers[field.Name] = () => password.Password;
				_writers[field.Name] = text => password.Password = text;
				input = password;
			}
			else
			{
				var text = new TextBox().PrimarySelection().MinWidth(260).Text(value);
				if (field.Placeholder is { Length: > 0 } placeholder) text.Placeholder(placeholder);
				_readers[field.Name] = () => text.Text;
				_writers[field.Name] = textBox => text.Text = textBox;
				input = text;
			}

			_firstInput ??= input;
			var group = new StackPanel().Spacing(2).Children(Label(field.Label), input);
			if (field.IsMainKey && _initialKey is not null)
			{
				group.Children(Hint(Strings.AuthKeyExistingHint));
			}
			_fields.Children(group);
		}

		var hints = new List<string>();
		if (_selected.HasMainKey) hints.Add(Strings.AuthKeyCommandHint);
		if (_selected.EnvironmentFields.Count > 0) hints.Add(Strings.AuthKeyEnvironmentHint);
		if (_status.Key?.Hint is { Length: > 0 } keyHint) hints.Add(keyHint);
		foreach (var hint in hints) _fieldHint.Children(Hint(hint));

		var label = _status.Key?.Label;
		_current.Text(label is { Length: > 0 } && !string.Equals(label, Strings.AuthKeyMethod, StringComparison.Ordinal)
			? label + " · " + _status.KeyStateText
			: _status.KeyStateText);
	}

	private void Save()
	{
		_error.Text(string.Empty);
		var key = _selected.HasMainKey ? _readers[ProviderKeyField.MainKey]().Trim() : null;
		if (string.IsNullOrEmpty(key) && _initialKey is { Length: > 0 }) key = _initialKey;
		if (_selected.HasMainKey && string.IsNullOrEmpty(key))
		{
			_error.Text(Strings.AuthKeyFieldRequired(Strings.ConfigKey));
			return;
		}

		var environment = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var field in _selected.EnvironmentFields)
		{
			var value = _readers[field.Name]().Trim();
			if (value.Length == 0) continue;
			environment[field.Name] = value;
		}

		foreach (var field in _selected.Fields.Where(static field => field.Required))
		{
			if (field.IsMainKey && !string.IsNullOrEmpty(key)) continue;
			if (!field.IsMainKey &&
				(environment.ContainsKey(field.Name) || ProviderEnvironment.EnvironmentValue(stored: null, field.Name) is not null)) continue;
			_error.Text(Strings.AuthKeyFieldRequired(field.Label));
			return;
		}

		if (_status.OAuthConfigured)
		{
			_ = ConfirmAndSaveAsync(key, environment);
			return;
		}

		SaveCore(key, environment, overwriteOAuth: false);
	}

	private async Task ConfirmAndSaveAsync(string? key, IReadOnlyDictionary<string, string> environment)
	{
		if (_confirming) return;
		_confirming = true;
		try
		{
			if (await MessageBox.ConfirmAsync(Strings.AuthKeyOverwriteOAuth))
			{
				SaveCore(key, environment, overwriteOAuth: true);
			}
		}
		finally
		{
			_confirming = false;
		}
	}

	private void SaveCore(string? key, IReadOnlyDictionary<string, string> environment, bool overwriteOAuth)
	{
		if (_saving) return;
		_saving = true;
		try
		{
			ProviderAuthService.SaveKey(_status.Id, key, environment, _agentDir, overwriteOAuth);
			_saved(this, false);
			Hide();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CodexAuthException)
		{
			_error.Text(Strings.AuthKeySaveFailed);
		}
		finally
		{
			_saving = false;
		}
	}

	private async Task ClearAsync()
	{
		if (!await MessageBox.ConfirmAsync(Strings.AuthKeyClearConfirm(_status.Name))) return;
		try
		{
			if (!ProviderAuthService.Logout(_status.Id, _agentDir))
			{
				_error.Text(Strings.AuthKeyClearFailed);
				return;
			}

			_saved(this, true);
			Hide();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or CodexAuthException)
		{
			_error.Text(Strings.AuthKeyClearFailed);
		}
	}

	public void Hide()
	{
		if (!_view.IsVisible || _fade.IsClosing) return;
		_fade.Close(CloseNow);
	}

	private void CloseNow()
	{
		_view.IsVisible(false);
		_window?.OverlayLayer.Remove(_view);
		_window = null;
	}

	internal void SelectMethodForSmoke(string methodId)
	{
		var option = _status.Key?.Option(methodId) ?? throw new ArgumentException($"unknown method {methodId}", nameof(methodId));
		_syncing = true;
		_method.SelectedIndex = OptionIndex(_status.Key!, option);
		_syncing = false;
		SelectOption(option);
	}

	internal void FillForSmoke(string? key, IReadOnlyDictionary<string, string>? environment = null)
	{
		if (key is not null && _writers.TryGetValue(ProviderKeyField.MainKey, out var writeKey)) writeKey(key);
		foreach (var (name, value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
		{
			if (_writers.TryGetValue(name, out var write)) write(value);
		}
	}

	internal void SaveForSmoke(bool overwriteOAuth = true)
	{
		if (_status.OAuthConfigured && !overwriteOAuth)
		{
			Save();
			return;
		}

		var key = _selected.HasMainKey ? _readers[ProviderKeyField.MainKey]().Trim() : null;
		if (string.IsNullOrEmpty(key) && _initialKey is { Length: > 0 }) key = _initialKey;
		var environment = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var field in _selected.EnvironmentFields)
		{
			var value = _readers[field.Name]().Trim();
			if (value.Length > 0) environment[field.Name] = value;
		}

		SaveCore(key, environment, overwriteOAuth);
	}

	private static TextBlock Label(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeCaption).PrimaryText();

	private static TextBlock Hint(string text) => new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro)
		.MutedText().TextWrapping(TextWrapping.Wrap);
}
