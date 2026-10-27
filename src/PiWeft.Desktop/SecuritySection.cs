using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Security;

namespace PiWeft.Desktop;

/// <summary>
/// 安全门设置面板（设置 → 运行 → 安全门）。与 PiDeck 的安全管理面板同构：
/// 总开关 + 默认档位 + 每档可展开编辑（工具动作、危险命令、目录边界、敏感文件），
/// 支持新建/删除自定义档位。
/// <para>
/// 两处刻意的差异：
/// <list type="bullet">
/// <item><description><strong>即时保存</strong>：每次改动直接写全局 <c>settings.json</c> 的
/// <c>security</c> 段，不走「草稿 + 统一保存」。PiDeck 需要草稿是因为它的确认动作（ask）
/// 依赖运行中的扩展热更新；本项目改档就是下一次工具调用生效，留草稿只会多一套脏状态与
/// 「切走丢掉未保存修改」的坑。</description></item>
/// <item><description>会话级覆盖不在这里管理，在会话输入框的「安全」按钮上（与 PiDeck 的
/// 分布一致）。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SecuritySection
{
	private readonly Func<string> _agentDir;
	private readonly Action _changed;
	private readonly StackPanel _body = new();
	private SecurityConfig _config = SecurityConfig.Off;
	private string? _expanded;
	private string? _error;

	/// <param name="agentDir">写全局设置时用；用函数是因为 agent 目录可以在弹窗打开后变。</param>
	/// <param name="changed">保存成功后的回调，宿主据此刷新所有已打开会话的档位。</param>
	public SecuritySection(Func<string> agentDir, Action changed)
	{
		_agentDir = agentDir;
		_changed = changed;
		_body.Spacing(AppTheme.Space3);
		Reload();
	}

	public UIElement View => _body;

	/// <summary>重新从磁盘读取并重建（宿主在别处改过配置后调用）。</summary>
	public void Reload()
	{
		try
		{
			_config = SecurityConfig.Load(_agentDir());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			// 读不出就按默认档位显示；错误在下面单独一行说明，不留空白面板。
			_error = exception.Message;
			_config = SecurityConfig.Off;
		}

		Rebuild();
	}

	private void Rebuild()
	{
		foreach (var child in _body.Children.ToArray())
		{
			UiLifetime.Release(child);
		}

		_body.Clear();

		_body.Children(Hint(Strings.SecurityScope));

		if (_error is { Length: > 0 } error)
		{
			_body.Children(new TextBlock().Text(Strings.SecurityLoadFailedText(error))
				.FontSize(AppTheme.FontSizeCaption).ErrorText().TextWrapping(TextWrapping.Wrap));
		}

		_body.Children(ToggleGroup());
		_body.Children(LevelsGroup());

		if (_config.SessionLevels.Count > 0)
		{
			_body.Children(SessionOverridesGroup());
		}
	}

	// ---------------------------------------------------------------- 总开关与默认档

	private Element ToggleGroup()
	{
		var enabled = new CheckBox().Content(new TextBlock()
			.Text(Strings.SecurityEnabled).FontSize(AppTheme.FontSizeCaption).MutedText());
		enabled.IsChecked = _config.Enabled;
		enabled.OnCheckedChanged(_ => Update(config => config with { Enabled = enabled.IsChecked == true }));

		var levels = LevelNames();
		var picker = new ComboBox().ItemsSource(ItemsView.Create(levels));
		picker.SelectedIndex = Math.Max(0, Array.IndexOf(levels, _config.DefaultLevelId));
		picker.SelectionChanged += _ =>
		{
			var index = picker.SelectedIndex;
			if (index >= 0 && index < levels.Length && levels[index] != _config.DefaultLevelId)
			{
				Update(config => config with { DefaultLevelId = levels[index] });
			}
		};

		return Card("shield", Strings.SecurityGroup, new StackPanel().Spacing(AppTheme.Space2).Children(
			Hint(Strings.SecurityEnabledHint),
			enabled,
			new DockPanel().LastChildFill().Children(
				picker.DockRight().MinWidth(200),
				FieldLabel(Strings.SecurityDefaultLevel))),
			enabledTip: Strings.SecurityEnabledTip);
	}

	// ---------------------------------------------------------------- 档位表

	private Element LevelsGroup()
	{
		var panel = new StackPanel().Spacing(AppTheme.Space2);

		foreach (var level in _config.Levels)
		{
			panel.Children(LevelCard(level));
		}

		panel.Children(AppIcons.TextButton("plus", Strings.SecurityNewLevel, CreateCustomLevel));

		return Card("lock", Strings.SecurityLevels, panel);
	}

	private Element LevelCard(ToolSecurity level)
	{
		var isDefault = string.Equals(level.Level, _config.DefaultLevelId, StringComparison.Ordinal);
		var expanded = _expanded == level.Level;

		var title = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(level.Name).FontSize(AppTheme.FontSizeCaption)
					.FontWeight(FontWeight.SemiBold).PrimaryText(),
				new TextBlock().Text(level.Level).FontFamily(AppTheme.MonoFont)
					.FontSize(AppTheme.FontSizeMicro).FaintText().VerticalAlignment(VerticalAlignment.Center),
				TagChip(isDefault
					? Strings.SecurityLevelDefault
					: level.Builtin ? Strings.SecurityLevelBuiltin : Strings.SecurityLevelCustom,
					isDefault));

		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1);
		if (!isDefault)
		{
			actions.Children(AppIcons.TextButton("check", Strings.SecurityMakeDefault,
				() => Update(config => config with { DefaultLevelId = level.Level })));
		}

		if (!level.Builtin)
		{
			actions.Children(AppIcons.Button("trash", Strings.SecurityDeleteLevel,
				() => Update(config => config.WithoutLevel(level.Level))));
		}

		actions.Children(new Button()
			.StyleName(AppStyles.GhostButton)
			.Height(AppTheme.ControlHeightSm)
			.Content(new TextBlock()
				.Text(expanded ? Strings.SecurityCollapse : Strings.SecurityExpand)
				.FontSize(AppTheme.FontSizeMicro).SubtleText())
			.OnClick(() =>
			{
				_expanded = expanded ? null : level.Level;
				Rebuild();
			}));

		var card = new StackPanel().Spacing(AppTheme.Space2).Children(
			new DockPanel().LastChildFill().Children(
				actions.DockRight(),
				title),
			new TextBlock().Text(level.Description).FontSize(AppTheme.FontSizeMicro).MutedText()
				.TextWrapping(TextWrapping.Wrap).IsVisible(level.Description.Length > 0));

		if (expanded)
		{
			card.Children(LevelEditor(level));
		}

		return new Border()
			.StyleName(AppStyles.CardMuted)
			.CornerRadius(AppTheme.RadiusSm)
			.Padding(new Thickness(AppTheme.Space3))
			.Child(card);
	}

	/// <summary>展开后的档位编辑器：路径边界、目录、危险命令、敏感文件、工具动作矩阵。</summary>
	private Element LevelEditor(ToolSecurity level)
	{
		var captured = level;
		var panel = new StackPanel().Spacing(AppTheme.Space3);

		// 敏感文件保护
		var protect = new CheckBox().Content(new TextBlock()
			.Text(Strings.SecurityProtectSensitive).FontSize(AppTheme.FontSizeCaption).MutedText());
		protect.IsChecked = captured.ProtectSensitivePaths;
		protect.OnCheckedChanged(_ => Update(config => config.WithLevel(captured with
		{
			ProtectSensitivePaths = protect.IsChecked == true,
		})));
		panel.Children(protect);

		// 路径边界
		var policies = new[] { ToolPathPolicy.Unrestricted, ToolPathPolicy.Workspace, ToolPathPolicy.Custom };
		var policyNames = Array.ConvertAll(policies, PolicyName);
		var policy = new ComboBox().ItemsSource(ItemsView.Create(policyNames));
		policy.SelectedIndex = Array.IndexOf(policies, captured.PathPolicy);
		policy.SelectionChanged += _ =>
		{
			var index = policy.SelectedIndex;
			if (index >= 0 && index < policies.Length && policies[index] != captured.PathPolicy)
			{
				Update(config => config.WithLevel(captured with { PathPolicy = policies[index] }));
			}
		};
		panel.Children(new DockPanel().LastChildFill().Children(
			policy.DockRight().MinWidth(200),
			FieldLabel(Strings.SecurityPathPolicy)));

		// 目录与命令表都是「一行一条」的多行文本；改动只在失去焦点时收一次，
		// 避免每敲一个字就写一遍设置文件。
		panel.Children(ListEditor(Strings.SecurityCustomAllowDirs, Strings.SecurityCustomAllowDirsHint,
			captured.CustomAllowDirs,
			values => Update(config => config.WithLevel(captured with { CustomAllowDirs = values }))));

		panel.Children(ListEditor(Strings.SecurityDenyDirs, Strings.SecurityDenyDirsHint,
			captured.DenyDirs,
			values => Update(config => config.WithLevel(captured with { DenyDirs = values }))));

		panel.Children(ListEditor(Strings.SecurityDenyBash, Strings.SecurityDenyBashHint,
			captured.DenyBashPatterns,
			values => Update(config => config.WithLevel(captured with { DenyBashPatterns = values }))));

		panel.Children(ToolActionsEditor(captured));

		if (!captured.Builtin)
		{
			var name = new TextBox().Text(captured.Name).Placeholder(Strings.SecurityLevelNamePlaceholder);
			name.Height(AppTheme.ControlHeightSm);
			var commit = AppIcons.TextButton("check", Strings.SecurityRename, () =>
			{
				var text = (name.Text ?? string.Empty).Trim();
				if (text.Length > 0 && text != captured.Name)
				{
					Update(config => config.WithLevel(captured with { Name = text }));
				}
			});
			panel.Children(new DockPanel().LastChildFill().Children(
				commit.DockRight(),
				name.Margin(new Thickness(0, 0, AppTheme.Space2, 0))));
		}

		return panel;
	}

	/// <summary>
	/// 工具动作矩阵。只列内置工具集里真实存在的工具 —— 列一堆本端口没有的工具
	/// 会让人以为配了就有用。
	/// </summary>
	private Element ToolActionsEditor(ToolSecurity level)
	{
		var panel = new StackPanel().Spacing(AppTheme.Space1);

		panel.Children(FieldLabel(Strings.SecurityToolActions));
		panel.Children(Hint(Strings.SecurityToolActionsHint));

		foreach (var tool in ToolSecurity.ManagedTools)
		{
			panel.Children(ActionRow(tool, ToolSecurity.LabelOf(tool), level.ActionOf(tool), action =>
				Update(config => config.WithLevel(WithAction(level, tool, action)))));
		}

		panel.Children(ActionRow("*", Strings.SecurityDefaultAction, level.DefaultAction, action =>
			Update(config => config.WithLevel(level with { DefaultAction = action }))));

		return panel;
	}

	private Element ActionRow(string tool, string label, string current, Action<string> choose)
	{
		var buttons = new StackPanel()
			.Orientation(Orientation.Horizontal)
			.Spacing(AppTheme.Space1);

		foreach (var action in new[] { ToolAction.Allow, ToolAction.Ask, ToolAction.Deny })
		{
			var value = action;
			buttons.Children(new Button()
				.StyleName(string.Equals(current, value, StringComparison.Ordinal)
					? AppStyles.SelectedButton : AppStyles.GhostButton)
				.Height(AppTheme.ControlHeightSm)
				.MinWidth(56)
				.Content(new TextBlock().Text(ActionName(value)).FontSize(AppTheme.FontSizeMicro)
					.HorizontalAlignment(HorizontalAlignment.Center))
				.OnClick(() => choose(value)));
		}

		return new DockPanel().LastChildFill().Children(
			buttons.DockRight(),
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
				.VerticalAlignment(VerticalAlignment.Center)
				.Children(
					new TextBlock().Text(label).FontSize(AppTheme.FontSizeCaption).MutedText(),
					new TextBlock().Text(tool).FontFamily(AppTheme.MonoFont)
						.FontSize(AppTheme.FontSizeMicro).FaintText()));
	}

	// ---------------------------------------------------------------- 会话覆盖

	private Element SessionOverridesGroup()
	{
		var panel = new StackPanel().Spacing(AppTheme.Space1);
		panel.Children(Hint(Strings.SecuritySessionHint));

		foreach (var (path, levelId) in _config.SessionLevels)
		{
			var captured = path;
			panel.Children(new DockPanel().LastChildFill().Children(
				AppIcons.Button("trash", Strings.SecurityClearOverride, () =>
					Update(config => config.WithSessionLevel(captured, levelId: null))).DockRight(),
				new TextBlock().Text($"{Path.GetFileNameWithoutExtension(path)} · {levelId}")
					.FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.NoWrap)));
		}

		return Card("git-branch", Strings.SecuritySessionLevels, panel);
	}

	// ---------------------------------------------------------------- 写盘

	private string[] LevelNames() => [.. _config.Levels.Select(level => level.Level)];

	private void CreateCustomLevel()
	{
		var stamp = Convert.ToBase64String(Guid.NewGuid().ToByteArray())[..4].ToLowerInvariant();
		var custom = ToolSecurity.Standard with
		{
			Level = $"custom-{stamp}",
			Name = $"{Strings.SecurityLevelDefaultName}-{stamp}",
			Description = string.Empty,
			Builtin = false,
		};

		_expanded = custom.Level;
		Update(config => config.WithLevel(custom) with { DefaultLevelId = custom.Level });
	}

	private static ToolSecurity WithAction(ToolSecurity level, string tool, string action)
	{
		var actions = new Dictionary<string, string>(level.ToolActions, StringComparer.Ordinal);
		// 与兜底动作相同的取值不再单列：档位形状更干净，写回设置文件也不会出现一堆冗余键。
		if (string.Equals(action, level.DefaultAction, StringComparison.Ordinal))
		{
			actions.Remove(tool);
		}
		else
		{
			actions[tool] = action;
		}

		return level with { ToolActions = actions };
	}

	/// <summary>
	/// 应用一次修改并落盘。写盘失败时恢复原配置并显示原因 —— 界面上显示的是「已生效」的
	/// 假象比报错更糟。
	/// </summary>
	private void Update(Func<SecurityConfig, SecurityConfig> mutate)
	{
		var previous = _config;
		var next = mutate(_config).Sanitized();
		try
		{
			SecurityConfig.Save(next, _agentDir());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			_error = exception.Message;
			Rebuild();
			return;
		}

		_config = next;
		_error = null;
		_changed();
		Rebuild();
	}

	private static string PolicyName(ToolPathPolicy policy) => policy switch
	{
		ToolPathPolicy.Workspace => Strings.SecurityPolicyWorkspace,
		ToolPathPolicy.Custom => Strings.SecurityPolicyCustom,
		_ => Strings.SecurityPolicyUnrestricted,
	};

	private static string ActionName(string action) => action switch
	{
		ToolAction.Ask => Strings.SecurityActionAsk,
		ToolAction.Deny => Strings.SecurityActionDeny,
		_ => Strings.SecurityActionAllow,
	};

	/// <summary>一行一条的列表编辑器；失焦时才写盘。</summary>
	private Element ListEditor(string title, string hint, IReadOnlyList<string> values, Action<IReadOnlyList<string>> commit)
	{
		var input = new MultiLineTextBox()
			.PrimarySelection()
			.StyleName(AppStyles.TextInput)
			.FontFamily(AppTheme.MonoFont)
			.FontSize(AppTheme.FontSizeMicro)
			.MinHeight(76)
			.MaxWidth(960);
		input.Text = string.Join('\n', values);

		var captured = values;
		var apply = AppIcons.TextButton("check", Strings.SecurityApplyList, () =>
		{
			var lines = (input.Text ?? string.Empty)
				.Split('\n')
				.Select(static line => line.Trim())
				.Where(static line => line.Length > 0)
				.ToList();
			if (!lines.SequenceEqual(captured, StringComparer.Ordinal))
			{
				commit(lines);
			}
		});

		return new StackPanel().Spacing(AppTheme.Space1).Children(
			FieldLabel(title),
			Hint(hint),
			input,
			apply);
	}

	/// <summary>档位标签：默认档用强调色，其余用最弱档，扫一眼就能认出当前默认。</summary>
	private static TextBlock TagChip(string text, bool accent)
	{
		var block = new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro).VerticalAlignment(VerticalAlignment.Center);
		return accent ? block.AccentText() : block.FaintText();
	}

	private static TextBlock FieldLabel(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeCaption).PrimaryText();

	private static TextBlock Hint(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeMicro).MutedText().TextWrapping(TextWrapping.Wrap);

	/// <summary>与其它设置分区同构的卡片外观（图标 + 标题 + 内容）。</summary>
	private static Element Card(string icon, string title, UIElement content, string? enabledTip = null) => new Border()
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
							.FontWeight(FontWeight.SemiBold).PrimaryText(),
						new TextBlock().Text(enabledTip ?? string.Empty).FontSize(AppTheme.FontSizeMicro)
							.FaintText().VerticalAlignment(VerticalAlignment.Center))),
			new Border().Padding(new Thickness(AppTheme.Space4))
				.Child(content)));
}
