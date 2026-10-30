using Aprillz.MewUI;

namespace PiWeft.Desktop;

/// <summary>一个可以重新绑定的快捷键（组合串形如 <c>Ctrl+Shift+P</c>）。</summary>
/// <param name="Id">持久化用的标识，改动它会让用户已有的绑定失效。</param>
/// <param name="Category">分组，设置面板按组展示。</param>
/// <param name="Description">中文说明。</param>
/// <param name="Default">默认组合串。</param>
public sealed record ShortcutDefinition(string Id, string Category, string Description, string Default);

/// <summary>解析后的按键组合。</summary>
/// <param name="Key">主键。</param>
/// <param name="Modifiers">修饰键。</param>
public sealed record ShortcutBinding(Key Key, ModifierKeys Modifiers)
{
	/// <summary>
	/// 解析组合串。接受 <c>Ctrl</c>/<c>Control</c>、<c>Shift</c>、<c>Alt</c>/<c>Option</c> 与
	/// 字母、数字、<c>F1</c>–<c>F24</c>、<c>Enter</c>、<c>Space</c>、方向键等 <see cref="Key"/> 名称。
	/// 非法输入返回 null，由调用方决定是退回默认值还是提示用户。
	/// </summary>
	public static ShortcutBinding? TryParse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var modifiers = ModifierKeys.None;
		Key? key = null;

		foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			switch (part.ToLowerInvariant())
			{
				case "ctrl" or "control":
					modifiers |= ModifierKeys.Control;
					continue;

				case "shift":
					modifiers |= ModifierKeys.Shift;
					continue;

				case "alt" or "option":
					modifiers |= ModifierKeys.Alt;
					continue;
			}

			if (key is not null)
			{
				// 一个组合里只能有一个主键。
				return null;
			}

			key = ParseKey(part);
			if (key is null)
			{
				return null;
			}
		}

		return key is { } parsed ? new ShortcutBinding(parsed, modifiers) : null;
	}

	/// <summary>单独解析主键，数字键 0–9 对应 <c>Key.D0</c>–<c>Key.D9</c>。</summary>
	private static Key? ParseKey(string part)
	{
		if (part.Length == 1 && char.IsAsciiDigit(part[0]))
		{
			return Key.D0 + (part[0] - '0');
		}

		return Enum.TryParse<Key>(part, ignoreCase: true, out var key) && key != Key.None ? key : null;
	}

	/// <summary>格式化成写回配置文件的形状。</summary>
	public override string ToString()
	{
		var parts = new List<string>(4);

		if (Modifiers.HasFlag(ModifierKeys.Control))
		{
			parts.Add("Ctrl");
		}

		if (Modifiers.HasFlag(ModifierKeys.Shift))
		{
			parts.Add("Shift");
		}

		if (Modifiers.HasFlag(ModifierKeys.Alt))
		{
			parts.Add("Alt");
		}

		parts.Add(KeyName(Key));

		return string.Join('+', parts);
	}

	/// <summary>数字键显示成 1–9 / 0，其它沿用枚举名。</summary>
	private static string KeyName(Key key) =>
		key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString();

	/// <summary>这次按键是否命中这个绑定（修饰键必须完全一致，避免 Ctrl+P 抢走 Ctrl+Shift+P）。</summary>
	public bool Matches(KeyEventArgs args) => args.Key == Key && Normalize(args.Modifiers) == Normalize(Modifiers);

	/// <summary>只保留我们支持的三个修饰键，忽略 Meta / CapsLock 之类的噪音。</summary>
	private static ModifierKeys Normalize(ModifierKeys modifiers) =>
		modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
}

/// <summary>
/// 快捷键表：内置默认值 + 用户在 <c>desktop.json</c> 里的覆盖。无法解析或未知标识的覆盖会被忽略并记入
/// <see cref="Ignored"/>，这样一份改坏的配置不会让界面失去所有快捷键。
/// </summary>
public sealed class ShortcutMap
{
	/// <summary>全部可绑定动作。Ctrl+数字切换标签是范围型动作，单独处理。</summary>
	public static IReadOnlyList<ShortcutDefinition> Definitions { get; } =
	[
		new(ShortcutActions.Palette, "通用", "命令面板", "Ctrl+P"),
		new(ShortcutActions.NewSession, "会话", "新建会话", "Ctrl+T"),
		new(ShortcutActions.OpenProject, "项目", "打开项目", "Ctrl+Shift+O"),
		new(ShortcutActions.Models, "会话", "选择模型", "Ctrl+M"),
		new(ShortcutActions.SearchSessions, "会话", "搜索会话", "Ctrl+K"),
		new(ShortcutActions.CloseTab, "标签", "关闭当前标签", "Ctrl+W"),
		new(ShortcutActions.Settings, "视图", "设置", "F1"),
		new(ShortcutActions.Git, "视图", "Git 面板", "Ctrl+O"),
		new(ShortcutActions.Files, "视图", "文件浏览器", "Ctrl+Shift+E"),
		new(ShortcutActions.Terminal, "视图", "底部终端", "Ctrl+Shift+D"),
		new(ShortcutActions.RightPanel, "视图", "右侧边栏", "Ctrl+J"),
		new(ShortcutActions.ToggleTheme, "视图", "切换深浅主题", "Ctrl+Shift+T"),
		new(ShortcutActions.Refresh, "视图", "刷新会话列表", "Ctrl+R"),
		new(ShortcutActions.Send, "会话", "发送输入框内容", "Enter"),
		new(ShortcutActions.Branches, "视图", "分支面板", "Ctrl+B"),
	];

	private readonly Dictionary<string, ShortcutBinding> _bindings = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
	private readonly List<string> _ignored = [];

	/// <summary>
	/// 窗口当前生效的那份表。旁注型的展示（等待提示轮播）不想把地图一层层传到构造函数里，
	/// 就由主窗口建表时挂在这里；没窗口（单测、自检）时用内置默认值。
	/// </summary>
	internal static ShortcutMap Shared { get; set; } = new();

	public ShortcutMap(IReadOnlyDictionary<string, string>? overrides = null)
	{
		foreach (var definition in Definitions)
		{
			var binding = ShortcutBinding.TryParse(definition.Default);
			if (binding is not null)
			{
				_bindings[definition.Id] = binding;
			}
		}

		if (overrides is null)
		{
			return;
		}

		foreach (var (id, text) in overrides)
		{
			if (!_bindings.ContainsKey(id))
			{
				_ignored.Add(id);
				continue;
			}

			if (ShortcutBinding.TryParse(text) is { } parsed)
			{
				_bindings[id] = parsed;
				_overrides[id] = parsed.ToString();
			}
			else
			{
				// 解析不了就保留默认值，并记下来让设置面板提示。
				_ignored.Add(id);
			}
		}
	}

	/// <summary>被忽略的覆盖项（未知标识或无法解析的组合）。</summary>
	public IReadOnlyList<string> Ignored => _ignored;

	/// <summary>用户改过的绑定（只含与默认不同的项），用于写回配置。</summary>
	public IReadOnlyDictionary<string, string> Overrides => _overrides;

	/// <summary>取某个动作当前的绑定；标识未知时返回 null。</summary>
	public ShortcutBinding? Get(string id) => _bindings.GetValueOrDefault(id);

	/// <summary>设置面板显示用的文本，例如 <c>Ctrl+P</c>；动作没绑定则返回「未绑定」。</summary>
	public string Describe(string id) => Get(id)?.ToString() ?? Strings.ShortcutUnbound;

	/// <summary>把某个动作改绑到新的组合串；解析失败返回 false 且不改变现状。</summary>
	public bool Rebind(string id, string text)
	{
		if (!_bindings.ContainsKey(id) || ShortcutBinding.TryParse(text) is not { } parsed)
		{
			return false;
		}

		_bindings[id] = parsed;
		_overrides[id] = parsed.ToString();
		return true;
	}

	/// <summary>恢复某个动作的默认绑定。</summary>
	public void Reset(string id)
	{
		_overrides.Remove(id);

		var definition = Definitions.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
		if (definition is not null && ShortcutBinding.TryParse(definition.Default) is { } binding)
		{
			_bindings[id] = binding;
		}
	}

	/// <summary>
	/// 找出撞在同一个组合上的动作。冲突不会让程序出错，但后注册的动作会永远抢不到按键，因此设置面板要提示。
	/// </summary>
	public IReadOnlyList<(string Id, string OtherId, string Binding)> Conflicts()
	{
		var conflicts = new List<(string, string, string)>();
		var seen = new Dictionary<ShortcutBinding, string>();

		foreach (var definition in Definitions)
		{
			if (Get(definition.Id) is not { } binding)
			{
				continue;
			}

			if (seen.TryGetValue(binding, out var other))
			{
				conflicts.Add((definition.Id, other, binding.ToString()));
			}
			else
			{
				seen[binding] = definition.Id;
			}
		}

		return conflicts;
	}
}

/// <summary>快捷键标识常量：持久化在 <c>desktop.json</c> 的 <c>shortcuts</c> 里，不要随意更名。</summary>
public static class ShortcutActions
{
	public const string Palette = "palette";
	public const string NewSession = "newSession";
	public const string OpenProject = "openProject";
	public const string Models = "models";
	public const string SearchSessions = "searchSessions";
	public const string CloseTab = "closeTab";
	public const string Settings = "settings";
	public const string Git = "git";
	public const string Files = "files";
	public const string Terminal = "terminal";
	public const string RightPanel = "rightPanel";
	public const string ToggleTheme = "toggleTheme";
	public const string Refresh = "refresh";
	public const string Send = "send";
	public const string Branches = "branches";
}
