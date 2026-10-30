namespace PiWeft.Desktop;

/// <summary>
/// 状态条右侧的功能提示轮播（PiDeck 的 <c>RespondingTips</c> 同义）。
///
/// 等待期是用户唯一的注意力空档：回复还没字可看，中断又太早。用这一行曝光
/// 「没有可见入口」的能力——命令面板、排队发送、@ 引用、提示音。
///
/// 收录规则（照 PiDeck）：
/// 1. 只写本应用真的做得到的事，能给出入口的才写；
/// 2. 键位不写死，只记动作标识，实际按键由生效的 <see cref="ShortcutMap"/> 渲染，用户改过键也不会说错；
/// 3. 一条一行、超长截断：它是旁注，不能把状态条撑宽。
/// </summary>
internal static class RespondingTips
{
	/// <summary>一条提示：正文里的 <see cref="Strings.RespondingTipShortcutToken"/> 换成生效键位。</summary>
	internal readonly record struct Tip(string Text, string? ShortcutId);

	/// <summary>轮播步进：比状态短语的 1.8s 慢一个量级，两处文字同时跳会很吵。</summary>
	public const double TipStepMs = 7000;

	private static readonly Tip[] Entries =
	[
		new(Strings.RespondingTipShortcutToken + " 打开命令面板，动作名直接敲就行", ShortcutActions.Palette),
		new(Strings.RespondingTipShortcutToken + " 搜索这个目录里的历史会话", ShortcutActions.SearchSessions),
		new(Strings.RespondingTipShortcutToken + " 开关右侧边栏，Git、文件与用量都在那一列", ShortcutActions.RightPanel),
		new(Strings.RespondingTipShortcutToken + " 在窗口底部开一个终端", ShortcutActions.Terminal),
		new(Strings.RespondingTipShortcutToken + " 换模型，不用离开当前会话", ShortcutActions.Models),
		new("运行中也能继续发：点发送键右侧的小箭头，把这条「加入当前回合」或「排队到下一轮」", null),
		new("输入 @ 引用项目里的文件，用 /skill:名称 显式触发技能", null),
		new("点下方的上下文百分比，看本轮首 token 延迟与生成速度", null),
		new("这一轮等太久了？运行中发送键就是「中断」，输入框立刻回到可用状态", null),
		new("设置 → 提示音：回复完成或出错时响一声，长任务不用盯着屏幕", null),
	];

	/// <summary>
	/// 随机起点：短等待往往撑不过一整轮，固定从第一条开始等于只有第一条有曝光。
	/// 进程内一次抽样，每回合再用回合起点错开一次；之后按本回合已用时间推进，
	/// 同一回合内读起来就是稳定的轮播，而不是每一拍乱跳。
	/// </summary>
	private static readonly int _offset = Random.Shared.Next(Entries.Length);

	/// <summary>读偏好（设置页关掉就不再出现）；测试可注入。</summary>
	public static Func<bool> EnabledProvider { get; set; } = static () => ReadPreference();

	/// <summary>键位表；窗口没建起来时（单测、自检）用默认绑定。</summary>
	public static Func<ShortcutMap> BindingsProvider { get; set; } = static () => ShortcutMap.Shared;

	/// <summary>是否显示提示本身（不影响提示里提到的功能）。首次读取后缓存，设置页保存时调
	/// <see cref="Invalidate"/> 失效：状态条一秒要问好几次，不能每次去碰一下 desktop.json。</summary>
	public static bool Enabled
	{
		get
		{
			if (Volatile.Read(ref _loaded))
			{
				return _cached;
			}

			_lock.Enter();
			try
			{
				if (_loaded)
				{
					return _cached;
				}

				try
				{
					_cached = EnabledProvider();
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
					or System.Text.Json.JsonException or ArgumentException or NotSupportedException or PathTooLongException)
				{
					// 读不到偏好（含 agent 目录路径本身有问题）就照常显示：这一行只是旁注，
					// 不值得为一个读不出来开关去改变状态条，更不能让渲染路径抛出去。
					_cached = true;
				}

				Volatile.Write(ref _loaded, true);
				return _cached;
			}
			finally
			{
				_lock.Exit();
			}
		}
	}

	private static readonly Lock _lock = new();
	private static bool _cached = true;
	private static bool _loaded;

	/// <summary>让下一次读取立刻反映新的偏好（设置页保存后调用，单测也用它重置）。</summary>
	public static void Invalidate() => Volatile.Write(ref _loaded, false);

	/// <summary>
	/// 当前这一条提示：<paramref name="elapsedMs"/> 是本回合已用时间，<paramref name="turnSeed"/> 是
	/// 本回合的单调起点（同一回合内稳定，换一回合换一个相位）。关掉开关或没有可说的内容时返回 null，
	/// 状态条那一段整块不占位。
	/// </summary>
	public static string? Current(double elapsedMs, long turnSeed = 0)
	{
		if (!Enabled || Entries.Length == 0)
		{
			return null;
		}

		// 回合起点当相位：每次等待都从不同的那一条开始，不会「短等待永远只看到同一句」。
		var phase = (int)((ulong)turnSeed % (ulong)Entries.Length);
		var start = (_offset + phase) % Entries.Length;
		var index = (start + (int)(Math.Max(0, elapsedMs) / TipStepMs)) % Entries.Length;
		return Render(Entries[index]);
	}

	/// <summary>取第 <paramref name="index"/> 条（诊断与单测用的确定入口）。</summary>
	public static string? At(int index)
	{
		if (index < 0 || index >= Entries.Length)
		{
			return null;
		}

		return Render(Entries[index]);
	}

	private static string Render(in Tip tip)
	{
		if (tip.ShortcutId is not { Length: > 0 } id)
		{
			return tip.Text;
		}

		// 键位取不到也不能把占位符露给用户：退化成不带键位的说法。
		var keys = Describe(id);
		return keys.Length == 0
			? tip.Text.Replace(Strings.RespondingTipShortcutToken + " ", string.Empty, StringComparison.Ordinal)
			: tip.Text.Replace(Strings.RespondingTipShortcutToken, keys, StringComparison.Ordinal);
	}

	private static string Describe(string id)
	{
		try
		{
			var text = BindingsProvider().Describe(id);
			// 「未绑定」不能进旁注：那一句读起来像错误提示。取不到键位就退化成不带键位的说法。
			return string.Equals(text, Strings.ShortcutUnbound, StringComparison.Ordinal) ? string.Empty : text;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return string.Empty;
		}
	}

	private static bool ReadPreference()
	{
		// 与提示音同一口径：读 desktop.json，由 DesktopPreference 的容错保证不抛。
		return DesktopPreference.Load().RespondingTips;
	}
}
