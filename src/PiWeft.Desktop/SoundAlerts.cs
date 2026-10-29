using System.Runtime.InteropServices;
using PiWeft.Cli;

namespace PiWeft.Desktop;

/// <summary>提示音的三类事件，与 PiDeck 的 <c>SoundAlertKind</c> 一致。</summary>
public enum SoundAlertKind
{
	/// <summary>会话正常完成。</summary>
	Done,

	/// <summary>会话以错误收尾。</summary>
	Error,

	/// <summary>代理停下来等用户输入（ask_question 之类）。</summary>
	Waiting,
}

/// <summary>单个事件的音效配置。</summary>
/// <param name="Enabled">该事件是否播放提示音。</param>
/// <param name="Preset">选中的预设音效。</param>
public sealed record SoundAlertEventSettings(bool Enabled, SoundAlertPreset Preset)
{
	/// <summary>该事件的出厂默认：完成/出错开、等待输入关（与 PiDeck 一致）。</summary>
	public static SoundAlertEventSettings Default(SoundAlertKind kind) => kind switch
	{
		SoundAlertKind.Done => new(true, SoundAlertPreset.DoneChime),
		SoundAlertKind.Error => new(true, SoundAlertPreset.ErrorBuzz),
		_ => new(false, SoundAlertPreset.WaitingPing),
	};

	/// <summary>该事件可选的预设（设置页的下拉项）。</summary>
	public static IReadOnlyList<SoundAlertPreset> Presets(SoundAlertKind kind) => kind switch
	{
		SoundAlertKind.Done => [SoundAlertPreset.DoneChime, SoundAlertPreset.DoneBell, SoundAlertPreset.DonePop],
		SoundAlertKind.Error => [SoundAlertPreset.ErrorBuzz, SoundAlertPreset.ErrorAlert],
		_ => [SoundAlertPreset.WaitingPing, SoundAlertPreset.WaitingKnock],
	};
}

/// <summary>
/// 提示音设置（PiDeck 的 <c>soundAlert</c> 同义）：总开关、音量、仅失焦，以及三类事件。
/// 每类事件开关与音效分开，允许「完成响、出错响、等待不响」这种常见组合。
/// </summary>
public sealed record SoundAlertSettings
{
	public bool Enabled { get; init; } = true;

	/// <summary>音量 0–1；非法值按 0.6 处理（与 PiDeck 的 clampSoundVolume 一致）。</summary>
	public double Volume { get; init; } = SoundPlayer.DefaultVolume;

	/// <summary>只在主窗口没有焦点时提醒（PiDeck 的 alertOnlyWhenUnfocused）。</summary>
	public bool OnlyWhenUnfocused { get; init; }

	public SoundAlertEventSettings Done { get; init; } = SoundAlertEventSettings.Default(SoundAlertKind.Done);

	public SoundAlertEventSettings Error { get; init; } = SoundAlertEventSettings.Default(SoundAlertKind.Error);

	public SoundAlertEventSettings Waiting { get; init; } = SoundAlertEventSettings.Default(SoundAlertKind.Waiting);

	public SoundAlertEventSettings For(SoundAlertKind kind) => kind switch
	{
		SoundAlertKind.Error => Error,
		SoundAlertKind.Waiting => Waiting,
		_ => Done,
	};

	public SoundAlertSettings With(SoundAlertKind kind, SoundAlertEventSettings settings) => kind switch
	{
		SoundAlertKind.Error => this with { Error = settings },
		SoundAlertKind.Waiting => this with { Waiting = settings },
		_ => this with { Done = settings },
	};
}

/// <summary>
/// 提示音调度：把会话状态边沿与等待输入翻译成「该不该响」，再交给 <see cref="SoundPlayer"/>。
///
/// 规则照搬 PiDeck 的 soundAlertTracker：
/// - 完成：同一会话 8 秒冷却（settled 与状态兜底可能双触发），并带 1.5 秒全局冷却，
///   多个会话同时完成时只响最近一次，避免叠音；
/// - 出错：只有「从非错误变成错误」才响，启动时已经挂着的错误不是新事件；
/// - 等待输入：同一会话的一批请求只响一次，全部回答完再重置，下次新批才再响；
/// - 仅失焦：主窗口在前台时整体静默（应用内 toast 不受影响）。
/// </summary>
internal sealed class SoundAlerts
{
	/// <summary>同一会话完成提醒的冷却：8 秒内只响一次。</summary>
	internal static readonly TimeSpan DoneCooldown = TimeSpan.FromSeconds(8);

	/// <summary>全局冷却：多个会话同时完成时只放行最近一次。</summary>
	internal static readonly TimeSpan GlobalCooldown = TimeSpan.FromMilliseconds(1500);

	private sealed record SessionState(bool Streaming, string? Error, DateTimeOffset DoneCooldownUntil);

	private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, HashSet<string>> _pendingRequests = new(StringComparer.Ordinal);
	private readonly HashSet<string> _waitingNotified = new(StringComparer.Ordinal);

	/// <summary>进程级单例：窗口与会话标签都往这里报。</summary>
	public static SoundAlerts Shared { get; } = new();

	/// <summary>读当前设置；默认读桌面偏好。</summary>
	public Func<SoundAlertSettings> SettingsProvider { get; set; } = static () => DesktopPreference.Load().SoundAlert;

	/// <summary>主窗口是否在前台；没有窗口时按失焦处理（宁可多响一次，也别吞掉提醒）。</summary>
	public Func<bool> IsWindowFocused { get; set; } = static () => false;

	/// <summary>实际播放（测试注入用）。</summary>
	public Action<SoundAlertPreset, double> Player { get; set; } = static (preset, volume) => SoundPlayer.Play(preset, volume);

	/// <summary>时钟（测试注入用）。</summary>
	public Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;

	private DateTimeOffset _lastPlayedAt = DateTimeOffset.MinValue;
	/// <summary>设置缓存：StateChanged 可能一秒报几次，不必每次都读一遍 desktop.json。</summary>
	private SoundAlertSettings? _cachedSettings;
	private DateTimeOffset _cachedSettingsAt = DateTimeOffset.MinValue;

	/// <summary>
	/// 报告一个会话的运行态。由宿在会话状态变化时调用（同一状态重复调用是安全的）：
	/// 只在「流式 → 空闲」与非错误 → 错误的边沿上响。
	/// </summary>
	/// <param name="sessionId">会话标识；空串表示无法归属（直接忽略）。</param>
	/// <param name="streaming">该会话是否正在运行。</param>
	/// <param name="error">当前错误文本；无错误传 null。</param>
	public void Observe(string? sessionId, bool streaming, string? error)
	{
		if (string.IsNullOrEmpty(sessionId) || !Enabled(out var settings))
		{
			return;
		}

		var now = Clock();
		var hasError = !string.IsNullOrWhiteSpace(error);
		var state = _sessions.TryGetValue(sessionId, out var known) ? known : null;
		// 首次见到这个会话只建基线：启动时已经结束的会话不是新事件。
		if (state is null)
		{
			_sessions[sessionId] = new SessionState(streaming, error, DateTimeOffset.MinValue);
			if (!hasError)
			{
				return;
			}

			// 首帧就带错误（用户刚发出一条必然失败的消息）仍然要提醒。
			Play(SoundAlertKind.Error, settings, now);
			return;
		}
		else
		{
			if (hasError && !string.Equals(state.Error, error, StringComparison.Ordinal))
			{
				Play(SoundAlertKind.Error, settings, now);
			}
			else if (!hasError && state.Streaming && !streaming && now >= state.DoneCooldownUntil)
			{
				// 完成响过一次后 8 秒内不再响：settled 与状态兜底会重复报到同一轮结束。
				_sessions[sessionId] = new SessionState(streaming, error, now + DoneCooldown);
				Play(SoundAlertKind.Done, settings, now);
				return;
			}
		}

		_sessions[sessionId] = new SessionState(streaming, error, state?.DoneCooldownUntil ?? DateTimeOffset.MinValue);
	}

	/// <summary>代理开始等待用户输入：同一会话的一批请求只响一次。</summary>
	public void RequestWaiting(string? sessionId, string? requestId)
	{
		if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(requestId) || !Enabled(out var settings))
		{
			return;
		}

		if (!_pendingRequests.TryGetValue(sessionId, out var pending))
		{
			pending = new HashSet<string>(StringComparer.Ordinal);
			_pendingRequests[sessionId] = pending;
		}

		pending.Add(requestId);
		if (!_waitingNotified.Add(sessionId))
		{
			return;
		}

		Play(SoundAlertKind.Waiting, settings, Clock());
	}

	/// <summary>一条等待输入已经回答（或取消）：这一批清空后才允许下一次提醒。</summary>
	public void CompleteWaiting(string? sessionId, string? requestId)
	{
		if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(requestId))
		{
			return;
		}

		if (!_pendingRequests.TryGetValue(sessionId, out var pending) || !pending.Remove(requestId))
		{
			return;
		}

		if (pending.Count == 0)
		{
			_pendingRequests.Remove(sessionId);
			_waitingNotified.Remove(sessionId);
		}
	}

	/// <summary>会话关闭：清掉它的残留状态，避免 id 复用时误判边沿。</summary>
	public void Forget(string? sessionId)
	{
		if (string.IsNullOrEmpty(sessionId))
		{
			return;
		}

		_sessions.Remove(sessionId);
		_pendingRequests.Remove(sessionId);
		_waitingNotified.Remove(sessionId);
	}

	/// <summary>清空全部状态（测试与退出路径用）。</summary>
	public void Reset()
	{
		_sessions.Clear();
		_pendingRequests.Clear();
		_waitingNotified.Clear();
		_lastPlayedAt = DateTimeOffset.MinValue;
		_cachedSettings = null;
		_cachedSettingsAt = DateTimeOffset.MinValue;
	}

	/// <summary>读设置（2 秒缓存）：设置页改完最多 2 秒内生效，测试注入的 provider 仍立即生效。</summary>
	private SoundAlertSettings ReadSettings()
	{
		var now = Clock();
		if (_cachedSettings is not null && now - _cachedSettingsAt < TimeSpan.FromSeconds(2))
		{
			return _cachedSettings;
		}

		_cachedSettings = SettingsProvider() ?? new SoundAlertSettings();
		_cachedSettingsAt = now;
		return _cachedSettings;
	}

	/// <summary>
	/// 设置门控：总开关、该类事件开关、仅失焦，任一关掉都不响。
	/// 返回 false 时调用方直接放弃（不更新冷却，避免「关着开关也消耗冷却」）。
	/// </summary>
	private bool Enabled(out SoundAlertSettings settings)
	{
		settings = ReadSettings();
		if (!settings.Enabled)
		{
			return false;
		}

		return !settings.OnlyWhenUnfocused || !IsWindowFocused();
	}

	private void Play(SoundAlertKind kind, SoundAlertSettings settings, DateTimeOffset now)
	{
		var config = settings.For(kind);
		if (!config.Enabled)
		{
			return;
		}

		// 全局冷却：多个会话同时完成/失败时只响最近一次，避免叠成噪音。
		if (_lastPlayedAt != DateTimeOffset.MinValue && now - _lastPlayedAt < GlobalCooldown)
		{
			return;
		}

		_lastPlayedAt = now;
		try
		{
			Player(config.Preset, SoundPlayer.ClampVolume(settings.Volume));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException)
		{
			// 提醒是尽力而为：播放失败只留痕，不影响会话与界面。
			AppLog.Failure(AppLog.For<SoundAlerts>(), "Sound alert", ex);
		}
	}
}

/// <summary>
/// 提示音设置的 JSON 形状（<c>desktop.json</c> 的 <c>soundAlert</c> 节点）。
///
/// 键名与取值沿用 PiDeck：预设音效记 id 字符串（<c>done-chime</c> 等），
/// 这样两边的设置可以互相看懂，也方便用户照文档手改。
/// 解析永远不抛：缺字段、类型不对、预设名失效都回落到该事件的默认值。
/// </summary>
internal static class SoundAlertJson
{
	public static SoundAlertSettings Parse(System.Text.Json.Nodes.JsonNode? node)
	{
		var defaults = new SoundAlertSettings();
		if (node is not System.Text.Json.Nodes.JsonObject root)
		{
			return defaults;
		}

		return new SoundAlertSettings
		{
			Enabled = Bool(root, "enabled", defaults.Enabled),
			Volume = Number(root, "volume", defaults.Volume),
			OnlyWhenUnfocused = Bool(root, "onlyWhenUnfocused", defaults.OnlyWhenUnfocused),
			Done = Event(root, "done", SoundAlertKind.Done),
			Error = Event(root, "error", SoundAlertKind.Error),
			Waiting = Event(root, "waiting", SoundAlertKind.Waiting),
		};
	}

	public static System.Text.Json.Nodes.JsonObject Write(SoundAlertSettings settings) => new()
	{
		["enabled"] = settings.Enabled,
		["volume"] = Math.Round(SoundPlayer.ClampVolume(settings.Volume), 2),
		["onlyWhenUnfocused"] = settings.OnlyWhenUnfocused,
		["done"] = WriteEvent(settings.Done),
		["error"] = WriteEvent(settings.Error),
		["waiting"] = WriteEvent(settings.Waiting),
	};

	/// <summary>预设的稳定 id（PiDeck 同名）。</summary>
	public static string Id(SoundAlertPreset preset) => preset switch
	{
		SoundAlertPreset.DoneChime => "done-chime",
		SoundAlertPreset.DoneBell => "done-bell",
		SoundAlertPreset.DonePop => "done-pop",
		SoundAlertPreset.ErrorBuzz => "error-buzz",
		SoundAlertPreset.ErrorAlert => "error-alert",
		SoundAlertPreset.WaitingPing => "waiting-ping",
		_ => "waiting-knock",
	};

	/// <summary>预设的界面名称。</summary>
	public static string Label(SoundAlertPreset preset) => preset switch
	{
		SoundAlertPreset.DoneChime => "清脆铃音",
		SoundAlertPreset.DoneBell => "钟声",
		SoundAlertPreset.DonePop => "轻点",
		SoundAlertPreset.ErrorBuzz => "警示蜂鸣",
		SoundAlertPreset.ErrorAlert => "错误警报",
		SoundAlertPreset.WaitingPing => "等待提示",
		_ => "敲门",
	};

	/// <summary>id → 预设；无法识别返回 null（调用方回落默认）。</summary>
	public static SoundAlertPreset? Preset(string? id) => id switch
	{
		"done-chime" => SoundAlertPreset.DoneChime,
		"done-bell" => SoundAlertPreset.DoneBell,
		"done-pop" => SoundAlertPreset.DonePop,
		"error-buzz" => SoundAlertPreset.ErrorBuzz,
		"error-alert" => SoundAlertPreset.ErrorAlert,
		"waiting-ping" => SoundAlertPreset.WaitingPing,
		"waiting-knock" => SoundAlertPreset.WaitingKnock,
		_ => null,
	};

	private static System.Text.Json.Nodes.JsonObject WriteEvent(SoundAlertEventSettings settings) => new()
	{
		["enabled"] = settings.Enabled,
		["sound"] = Id(settings.Preset),
	};

	private static SoundAlertEventSettings Event(System.Text.Json.Nodes.JsonObject root, string key, SoundAlertKind kind)
	{
		var fallback = SoundAlertEventSettings.Default(kind);
		if (root[key] is not System.Text.Json.Nodes.JsonObject node)
		{
			return fallback;
		}

		var sound = node["sound"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text)
			? Preset(text)
			: null;
		return new SoundAlertEventSettings(
			Bool(node, "enabled", fallback.Enabled),
			// 预设名非法时回落到该事件的默认音效：开着开关却引用一个不存在的音效等于静默失效。
			sound is { } found && SoundAlertEventSettings.Presets(kind).Contains(found) ? found : fallback.Preset);
	}

	private static bool Bool(System.Text.Json.Nodes.JsonObject node, string key, bool fallback) =>
		node[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;

	private static double Number(System.Text.Json.Nodes.JsonObject node, string key, double fallback) =>
		node[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<double>(out var result) &&
		double.IsFinite(result) ? SoundPlayer.ClampVolume(result) : SoundPlayer.ClampVolume(fallback);
}
