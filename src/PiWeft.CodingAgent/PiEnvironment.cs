namespace PiWeft.CodingAgent;

/// 进程级环境开关，对应 pi 的 <c>PI_OFFLINE</c> 与 <c>settings.httpProxy</c>。
/// 两者都必须在任何 HTTP 客户端创建之前生效：代理来自 <c>HttpClient.DefaultProxy</c>
/// 首次读取时的环境变量，晚设置等于没设置。
public static class PiEnvironment
{
	public const string OfflineVariable = "PI_OFFLINE";

	/// 离线模式：只在启动期设置，之后不变。离线时跳过「自动/批量」网络操作
	/// （模型目录刷新、包更新、启动期目录拉取），用户显式发起的登录、安装与对话请求照常。
	public static bool Offline { get; internal set; } = IsTruthy(Environment.GetEnvironmentVariable(OfflineVariable));

	public static void Configure(bool offlineArgument = false)
	{
		// 每次按「参数 + 环境变量」重新求值，不 OR 旧值：进程内重置（测试、宿主重启）
		// 需要能关掉；真正进入离线时下面会把环境变量写成 1，所以重复调用仍然是幂等的。
		Offline = offlineArgument || IsTruthy(Environment.GetEnvironmentVariable(OfflineVariable));
		if (Offline)
		{
			Environment.SetEnvironmentVariable(OfflineVariable, "1");
		}
	}

	internal static void ResetForTests()
	{
		Offline = IsTruthy(Environment.GetEnvironmentVariable(OfflineVariable));
	}

	/// 把 <c>settings.httpProxy</c> 应用到 <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>。
	/// 与 pi 一致：只在配置了非空值时设置，且不清理用户已有的环境变量。
	/// 同时标记这组代理由本进程注入：agent shell 命令会清掉这些变量，
	/// 避免命令走模型专用出口（PiDeck 的 shell proxy 语义）。
	public static void ApplyHttpProxy(string? httpProxy)
	{
		var proxy = httpProxy?.Trim();
		if (string.IsNullOrEmpty(proxy))
		{
			return;
		}

		Environment.SetEnvironmentVariable("HTTP_PROXY", proxy);
		Environment.SetEnvironmentVariable("HTTPS_PROXY", proxy);
		Environment.SetEnvironmentVariable("PIDECK_PI_PROXY_SCOPE", "model-only");
	}

	private static bool IsTruthy(string? value) =>
		value is not null &&
		(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
			value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
