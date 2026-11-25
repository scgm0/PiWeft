using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Security;

namespace PiWeft.Desktop;

/// 安全门设置面板（设置 → 运行 → 安全门）。与 PiDeck 的安全管理面板同构：
/// 总开关 + 默认档位 + 每档可展开编辑（工具动作、危险命令、目录边界、敏感文件），
/// 支持新建/删除自定义档位。
/// 两处刻意的差异：
/// <list type="bullet">
/// <item><description><strong>即时保存</strong>：每次改动直接写全局 <c>settings.json</c> 的
/// <c>security</c> 段，不走「草稿 + 统一保存」。PiDeck 需要草稿是因为它的确认动作（ask）
/// 依赖运行中的扩展热更新；本项目改档就是下一次工具调用生效，留草稿只会多一套脏状态与
/// 「切走丢掉未保存修改」的坑。</description></item>
/// <item><description>会话级覆盖不在这里管理，在会话输入框的「安全」按钮上（与 PiDeck 的
/// 分布一致）。</description></item>
/// </list>
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



	/// <summary>测试用：展开第一档，覆盖编辑器的构建路径。</summary>
	internal void ExpandFirstLevelForTest()
	{
		_expanded = _config.Levels.FirstOrDefault()?.Level;
		Rebuild();
	}

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

		if (_error is { Length: > 0 } error)
		{
			_body.Children(SettingsUi.Alert(Strings.SecurityLoadFailedText(error), AppStyles.RoleError));
		}

		_body.Children(ToggleGroup());
		_body.Children(LevelsGroup());

		if (_config.SessionLevels.Count > 0)
		{
			_body.Children(SessionOverridesGroup());
		}
	}


	private Element ToggleGroup()
	{
		var rows = new List<UIElement>();
		var enabled = SettingsUi.SwitchRow(
			Strings.SecurityEnabled, Strings.SecurityEnabledHint, _config.Enabled,
			value => Update(config => config with { Enabled = value }), out _);
		rows.Add(enabled);

		var levels = _config.Levels;
		var names = levels.Select(static level => level.Name).ToArray();
		var picker = new ComboBox().ItemsSource(ItemsView.Create(names)).Width(SettingsUi.ControlWidth);
		picker.SelectedIndex = Math.Max(0, levels.ToList().FindIndex(level =>
			string.Equals(level.Level, _config.DefaultLevelId, StringComparison.Ordinal)));
		picker.SelectionChanged += _ =>
		{
			var index = picker.SelectedIndex;
			if (index >= 0 && index < levels.Count && levels[index].Level != _config.DefaultLevelId)
			{
				Update(config => config with { DefaultLevelId = levels[index].Level });
			}
		};
		rows.Add(SettingsUi.Row(Strings.SecurityDefaultLevel, Strings.SecurityDefaultLevelHint, picker));
		return SettingsUi.Group("shield", Strings.SecurityToggleGroup, SettingsUi.Rows(rows),
			description: Strings.SecurityScope);
	}

	private Element LevelsGroup()
	{
		var body = new StackPanel();
		var first = true;
		foreach (var level in _config.Levels)
		{
			body.Children(SettingsUi.DividerRow(LevelCard(level), !first));
			first = false;
		}

		body.Children(SettingsUi.DividerRow(
			SmallTextButton(Strings.SecurityNewLevel, CreateCustomLevel, "plus"), topLine: true));
		return SettingsUi.Group("lock", Strings.SecurityLevels, body,
			description: Strings.SecurityLevelsHint,
			meta: _config.Levels.Count.ToString());
	}

	private UIElement LevelCard(ToolSecurity level)
	{
		var isDefault = string.Equals(level.Level, _config.DefaultLevelId, StringComparison.Ordinal);
		var expanded = _expanded == level.Level;

		var title = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
			.VerticalAlignment(VerticalAlignment.Center)
			.Children(
				new TextBlock().Text(level.Name).FontSize(AppTheme.FontSizeCaption)
					.FontWeight(FontWeight.SemiBold).PrimaryText()
					.TextTrimming(TextTrimming.CharacterEllipsis).MaxWidth(320),
				new TextBlock().Text(level.Level).FontFamily(AppTheme.MonoFont)
					.FontSize(AppTheme.FontSizeMicro).FaintText().VerticalAlignment(VerticalAlignment.Center),
				SettingsUi.Badge(isDefault
					? Strings.SecurityLevelDefault
					: level.Builtin ? Strings.SecurityLevelBuiltin : Strings.SecurityLevelCustom,
					isDefault ? AppStyles.RoleAccent : null));

		var actions = new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
			.VerticalAlignment(VerticalAlignment.Center);
		if (!isDefault)
		{
			actions.Children(SmallTextButton(Strings.SecurityMakeDefault,
				() => Update(config => config with { DefaultLevelId = level.Level }), "check"));
		}

		if (!level.Builtin)
		{
			actions.Children(SettingsUi.IconAction("trash", Strings.SecurityDeleteLevel,
				() => Update(config => config.WithoutLevel(level.Level))));
		}

		actions.Children(SmallTextButton(expanded ? Strings.SecurityCollapse : Strings.SecurityExpand, () =>
		{
			_expanded = expanded ? null : level.Level;
			Rebuild();
		}, null));

		var head = new Grid().Columns("*,auto").Spacing(AppTheme.Space3)
			.Children(title.Column(0), actions.Column(1));
		var card = new StackPanel().Spacing(AppTheme.Space2)
			.Padding(new Thickness(0, AppTheme.Space2, 0, AppTheme.Space2))
			.Children(head);
		if (level.Description.Length > 0)
		{
			card.Children(SettingsUi.Hint(level.Description));
		}

		if (expanded)
		{
			card.Children(new Border().BorderThickness(new Thickness(0, 1, 0, 0))
				.Padding(new Thickness(0, AppTheme.Space3, 0, 0))
				.Child(LevelEditor(level))
				.WithTheme(static (theme, border) => border.BorderBrush = AppTheme.For(theme).Border));
		}

		return card;
	}

	private UIElement LevelEditor(ToolSecurity level)
	{
		var captured = level;
		var panel = new StackPanel().Spacing(AppTheme.Space3);

		// 敏感文件保护
		panel.Children(SettingsUi.SwitchRow(
			Strings.SecurityProtectSensitive, null, captured.ProtectSensitivePaths,
			value => Update(config => config.WithLevel(captured with
			{
				ProtectSensitivePaths = value,
			})), out _));

		// 路径边界
		var policies = new[] { ToolPathPolicy.Unrestricted, ToolPathPolicy.Workspace, ToolPathPolicy.Custom };
		var policyNames = Array.ConvertAll(policies, PolicyName);
		var policy = new ComboBox().ItemsSource(ItemsView.Create(policyNames)).Width(SettingsUi.ControlWidth);
		policy.SelectedIndex = Array.IndexOf(policies, captured.PathPolicy);
		policy.SelectionChanged += _ =>
		{
			var index = policy.SelectedIndex;
			if (index >= 0 && index < policies.Length && policies[index] != captured.PathPolicy)
			{
				Update(config => config.WithLevel(captured with { PathPolicy = policies[index] }));
			}
		};
		panel.Children(SettingsUi.Row(Strings.SecurityPathPolicy, null, policy));

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
			var name = new TextBox().PrimarySelection().Text(captured.Name)
				.Placeholder(Strings.SecurityLevelNamePlaceholder)
				.StyleName(AppStyles.TextInputLine)
				.Height(AppTheme.ControlHeightSm)
				.Width(SettingsUi.ControlWidth);
			var commit = SettingsUi.IconAction("check", Strings.SecurityRename, () =>
			{
				var text = (name.Text ?? string.Empty).Trim();
				if (text.Length > 0 && text != captured.Name)
				{
					Update(config => config.WithLevel(captured with { Name = text }));
				}
			});
			panel.Children(SettingsUi.Row(Strings.SecurityLevelName,
				null,
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
					.VerticalAlignment(VerticalAlignment.Center).Children(name, commit)));
		}

		return panel;
	}

	/// 工具动作矩阵。只列内置工具集里真实存在的工具；列本端口没有的工具，
	/// 会让人以为配了就有用。
	private UIElement ToolActionsEditor(ToolSecurity level)
	{
		var panel = new StackPanel().Spacing(AppTheme.Space1);
		panel.Children(SettingsUi.Field(Strings.SecurityToolActions,
			SettingsUi.Hint(Strings.SecurityToolActionsHint)));

		foreach (var tool in ToolSecurity.ManagedTools)
		{
			panel.Children(ActionRow(tool, ToolSecurity.LabelOf(tool), level.ActionOf(tool), action =>
				Update(config => config.WithLevel(WithAction(level, tool, action)))));
		}

		panel.Children(ActionRow("*", Strings.SecurityDefaultAction, level.DefaultAction, action =>
			Update(config => config.WithLevel(level with { DefaultAction = action }))));

		return panel;
	}

	private UIElement ActionRow(string tool, string label, string current, Action<string> choose)
	{
		var buttons = new StackPanel().Orientation(Orientation.Horizontal).Spacing(2)
			.VerticalAlignment(VerticalAlignment.Center);
		foreach (var action in new[] { ToolAction.Allow, ToolAction.Ask, ToolAction.Deny })
		{
			var value = action;
			buttons.Children(new Button()
				.StyleName(string.Equals(current, value, StringComparison.Ordinal)
					? AppStyles.SelectedButton : AppStyles.GhostButton)
				.Height(AppTheme.ControlHeightSm)
				.MinWidth(52)
				.Padding(new Thickness(AppTheme.Space2, 0, AppTheme.Space2, 0))
				.Content(new TextBlock().Text(ActionName(value)).FontSize(AppTheme.FontSizeMicro)
					.HorizontalAlignment(HorizontalAlignment.Center))
				.OnClick(() => choose(value)));
		}

		return new Grid().Columns("*,auto").Spacing(AppTheme.Space3)
			.Padding(new Thickness(0, 3, 0, 3))
			.Children(
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space2)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						new TextBlock().Text(label).FontSize(AppTheme.FontSizeCaption).MutedText(),
						new TextBlock().Text(tool).FontFamily(AppTheme.MonoFont)
							.FontSize(AppTheme.FontSizeMicro).FaintText()).Column(0),
				buttons.Column(1));
	}

	private Element SessionOverridesGroup()
	{
		var rows = new List<UIElement>();
		foreach (var (path, levelId) in _config.SessionLevels)
		{
			var captured = path;
			var name = Path.GetFileNameWithoutExtension(path);
			var title = new StackPanel().Spacing(2).Children(
				new TextBlock().Text(name).FontSize(AppTheme.FontSizeCaption).PrimaryText(),
				new TextBlock().Text(path).FontFamily(AppTheme.MonoFont).FontSize(AppTheme.FontSizeMicro)
					.FaintText().TextTrimming(TextTrimming.CharacterEllipsis).HoverTip(path));
			rows.Add(new Grid().Columns("*,auto").Spacing(AppTheme.Space3).Children(
				title.Column(0),
				new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1)
					.VerticalAlignment(VerticalAlignment.Center)
					.Children(
						SettingsUi.Badge(levelId, AppStyles.RoleAccent),
						SettingsUi.IconAction("trash", Strings.SecurityClearOverride,
							() => Update(config => config.WithSessionLevel(captured, levelId: null))))
					.Column(1)));
		}

		return SettingsUi.Group("git-branch", Strings.SecuritySessionLevels, SettingsUi.Rows(rows),
			description: Strings.SecuritySessionHint,
			meta: rows.Count.ToString());
	}

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

	/// 应用一次修改并落盘。写盘失败时恢复原配置并显示原因；界面显示「已生效」的
	/// 假象比报错更糟。
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

	private UIElement ListEditor(string title, string hint, IReadOnlyList<string> values, Action<IReadOnlyList<string>> commit)
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
			SettingsUi.Hint(hint),
			input,
			apply);
	}

	/// 行内小动作：12px 文本 + 可选图标，不用主按钮的视觉重量。
	private static Button SmallTextButton(string text, Action action, string? icon)
	{
		var content = new StackPanel().Orientation(Orientation.Horizontal).Spacing(4)
			.VerticalAlignment(VerticalAlignment.Center);
		if (icon is { Length: > 0 }) content.Children(AppIcons.Icon(icon, AppStyles.RoleSubtle, 12));
		content.Children(new TextBlock().Text(text).FontSize(AppTheme.FontSizeMicro)
			.MutedText().VerticalAlignment(VerticalAlignment.Center));
		return new Button().Ghost().Content(content).Padding(new Thickness(AppTheme.Space1, 0, AppTheme.Space1, 0))
			.Height(AppTheme.ControlHeightSm).OnClick(action);
	}



	private static TextBlock FieldLabel(string text) => new TextBlock().Text(text)
		.FontSize(AppTheme.FontSizeCaption).PrimaryText();


}
