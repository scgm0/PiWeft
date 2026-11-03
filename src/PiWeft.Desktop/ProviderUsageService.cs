using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

/// <summary>一次探测计划：目标基址、凭据与按顺序尝试的候选。</summary>
internal sealed record UsageProbePlan(string BaseUrl, string Token, IReadOnlyList<UsageProbeCandidate> Candidates, string? Error = null);

/// <summary>单次探测失败的明细（URL + 状态/结构/网络），用于全部失败时生成可排查的提示。</summary>
internal sealed record UsageProbeAttempt(string Url, string Method, int? Status = null, string? Body = null, bool Shape = false, string? Error = null);

/// <summary>
/// 供应商用量/余额探测：逐候选、逐 URL 尝试，首个解析成功的结果即返回。
/// 凭据只在主进程发请求；响应原文不落日志、不回传 UI，只保留脱敏截断后的尝试明细。
/// </summary>
internal sealed class ProviderUsageService(HttpClient client)
{
	// 总超时由每次探测的 CancellationTokenSource 控制（按 provider 配置 1-300 秒），
	// HttpClient 自身不设总时限，否则配置成 30 秒也会被 15 秒截断。
	public static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

	/// <summary>读取当前模型对应 provider 的配置并查询；未显式启用时快速返回「未开启」。</summary>
	public static async Task<ProviderUsageResult> QueryProviderAsync(Model model, string cwd, CancellationToken cancellationToken)
	{
		var agentDir = SessionPaths.GetAgentDir();
		var document = UsageProbeStore.Load(agentDir);
		var config = document.Providers.GetValueOrDefault(model.Provider);
		var legacy = UsageProbeStore.LoadLegacyCandidate(agentDir, model.Provider);
		// 兼容窗口：只有旧版 usage-queries.json、没有新配置时沿用旧规则（旧版原本就是自动查询）。
		if (config is null && legacy is null) return ProviderUsageResult.DisabledResult();
		if (config is { Enabled: false }) return ProviderUsageResult.DisabledResult();
		return await QueryAsync(model, config ?? new UsageProbeProviderConfig(), document, legacy, cwd, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>按给定配置查询（弹窗测试也走这里，忽略 enabled 开关）。</summary>
	public static Task<ProviderUsageResult> QueryAsync(Model model, UsageProbeProviderConfig config, UsageProbeDocument document,
		UsageProbeCandidate? legacy, string cwd, CancellationToken cancellationToken, HttpClient? client = null)
	{
		var plan = BuildPlan(model, config, document, legacy, cwd);
		if (plan.Error is { } error) return Task.FromResult(ProviderUsageResult.Failure(error));
		var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSecs, 1, 300));
		return new ProviderUsageService(client ?? Client).ProbeAsync(model, plan, timeout, config.IntervalMinutes, cancellationToken);
	}

	/// <summary>弹窗「测试」：不读 enabled 开关、不写配置，直接用草稿配置探测。</summary>
	public static async Task<ProviderUsageResult> TestAsync(Model model, UsageProbeProviderConfig config, string cwd,
		CancellationToken cancellationToken)
	{
		var agentDir = SessionPaths.GetAgentDir();
		var document = UsageProbeStore.Load(agentDir);
		var legacy = UsageProbeStore.LoadLegacyCandidate(agentDir, model.Provider);
		return await QueryAsync(model, config, document, legacy, cwd, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>构造探测计划：声明式模板优先；否则内置候选 + 旧 probes 数组自动匹配。</summary>
	public static UsageProbePlan BuildPlan(Model model, UsageProbeProviderConfig config, UsageProbeDocument document,
		UsageProbeCandidate? legacy, string cwd)
	{
		var baseUrl = model.BaseUrl.Trim();
		if (baseUrl.Length == 0) return new("", "", [], Strings.UsageNoBaseUrl);
		var token = config.ApiKey.Trim();
		if (token.Length == 0) token = ResolveKey(model, cwd) ?? string.Empty;

		if (config.Template.Length > 0)
		{
			if (UsageProbeStore.IsDeclarativeTemplate(config.Template))
				return Declarative(model, config, token);
			var effectiveBase = config.BaseUrl.Length > 0 ? config.BaseUrl : baseUrl;
			var apiType = UsageProbeStore.NormalizeApi(model.Api);
			var matching = UsageProbes.Candidates.Where(candidate => candidate.TemplateId == config.Template).ToArray();
			var builtin = matching.Where(candidate => UsageProbes.Applies(candidate, effectiveBase, apiType)).ToArray();
			if (builtin.Length == 0) builtin = matching;
			if (builtin.Length == 0) return new(effectiveBase, token, [], Strings.UsageUnsupported);
			return new(effectiveBase, token, builtin);
		}

		var api = UsageProbeStore.NormalizeApi(model.Api);
		var candidates = UsageProbes.Candidates
			.Where(candidate => UsageProbes.Applies(candidate, baseUrl, api))
			.ToList();
		candidates.AddRange(document.Probes.Where(candidate => UsageProbes.Applies(candidate, baseUrl, api)));
		// 旧版自定义配置在「自动识别」模式下优先：用户手填的字段路径比内置候选更贴近他的网关。
		// 它只在新配置显式选择了模板时才让位，避免保存一次开关就把旧端点弄丢。
		if (legacy is not null && config.Template.Length == 0) candidates.Insert(0, legacy);
		return candidates.Count == 0
			? new(baseUrl, token, [], Strings.UsageUnsupported)
			: new(config.BaseUrl.Length > 0 ? config.BaseUrl : baseUrl, token, candidates);
	}

	private static UsageProbePlan Declarative(Model model, UsageProbeProviderConfig config, string token)
	{
		var baseUrl = config.BaseUrl.Trim().Length > 0 ? config.BaseUrl.Trim() : model.BaseUrl.Trim();
		switch (config.Template)
		{
			case "general":
				return new(baseUrl, token,
				[
					new UsageProbeCandidate("/usage",
						Parse: new UsageProbeParse(UsageParseKind.Balance, ValuePath: "balance", CurrencyPath: "unit")),
				]);
			case "newapi":
			{
				var accessToken = config.AccessToken.Trim();
				var userId = config.UserId.Trim();
				if (accessToken.Length == 0 || userId.Length == 0) return new(baseUrl, token, [], Strings.UsageNewApiNeeds);
				// 管理端点（/api/user/self）挂在站点根，不在推理端点（常带 /v1）之下：剥离版本段。
				var management = UsageProbes.StripVersionPath(baseUrl);
				return new(management, token,
				[
					new UsageProbeCandidate("/api/user/self", NoVersionPath: true,
						Headers: new Dictionary<string, string>
						{
							["Authorization"] = "Bearer " + accessToken,
							["New-Api-User"] = userId,
						},
						Parse: new UsageProbeParse(UsageParseKind.Credits,
							UsedPath: "data.used_quota", RemainingPath: "data.quota", Scale: 500000)),
				]);
			}
			case "cookie":
			{
				var cookie = config.Cookie.Trim();
				var cookiePath = config.CookiePath.Trim();
				var valuePath = config.ValuePath.Trim();
				if (cookie.Length == 0 || cookiePath.Length == 0 || valuePath.Length == 0)
					return new(baseUrl, token, [], Strings.UsageCookieNeeds);
				var management = UsageProbes.StripVersionPath(baseUrl);
				return new(management, token,
				[
					new UsageProbeCandidate(cookiePath, NoVersionPath: true, NoBearer: true,
						Headers: new Dictionary<string, string> { ["Cookie"] = cookie },
						Parse: new UsageProbeParse(UsageParseKind.Balance, ValuePath: valuePath, CurrencyPath: config.CurrencyPath.Trim())),
				]);
			}
			case "sub2api":
				return new(baseUrl, token,
				[
					new UsageProbeCandidate("/usage", Parse: new UsageProbeParse(UsageParseKind.Custom, Resolver: "sub2api-usage")),
				]);
			default:
				return new(baseUrl, token, [], Strings.UsageUnsupported);
		}
	}

	/// <summary>逐候选、逐 URL 探测；链式预检、响应截断、重定向拒绝与异常收敛都在这一层。</summary>
	public async Task<ProviderUsageResult> ProbeAsync(Model model, UsageProbePlan plan, TimeSpan timeout, int intervalMinutes,
		CancellationToken cancellationToken)
	{
		var attempts = new List<UsageProbeAttempt>();
		var missingKey = false;
		var startedAt = DateTimeOffset.UtcNow;
		var baseUri = Uri.TryCreate(plan.BaseUrl, UriKind.Absolute, out var parsedBase) ? parsedBase : null;
		// 模型自定义头只回发给供应商自己的域：模板允许把用量端点覆盖到别的域，
		// 但那些自定义头（可能含鉴权/租户标）不应跟着过去。
		var modelOrigin = Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelBase)
			? modelBase.GetLeftPart(UriPartial.Authority) : string.Empty;

		foreach (var candidate in plan.Candidates)
		{
			// 链式预检（xAI 先查 identity 拿 userId）：失败则整个候选跳过。
			Dictionary<string, string> preflightHeaders = new(StringComparer.OrdinalIgnoreCase);
			if (candidate.Preflight is { } preflight)
			{
				var captured = await RunPreflightAsync(model, plan, preflight, timeout, cancellationToken).ConfigureAwait(false);
				if (captured is null) continue;
				preflightHeaders[preflight.CaptureHeader] = captured;
			}

			foreach (var url in UsageProbes.Urls(candidate, plan.BaseUrl))
			{
				if (!TryValidateUrl(url, baseUri, candidate.AbsoluteUrl.Length > 0, out var uri))
				{
					attempts.Add(new(url, candidate.Method, Error: Strings.UsageInvalidConfig));
					continue;
				}
				var sameOrigin = string.Equals(uri.GetLeftPart(UriPartial.Authority), modelOrigin, StringComparison.OrdinalIgnoreCase);
				var headers = UsageProbes.BuildHeaders(candidate, plan.Token, sameOrigin ? model.Headers : null, sameOrigin);
				foreach (var (name, value) in preflightHeaders) headers[name] = value;
				if (!candidate.NoBearer && !headers.ContainsKey("Authorization") && !headers.ContainsKey("Cookie"))
				{
					missingKey = true;
					attempts.Add(new(url, candidate.Method, Error: Strings.UsageMissingKey));
					continue;
				}
				using var request = new HttpRequestMessage(new HttpMethod(candidate.Method), uri);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
				if (candidate.Method == "POST" && candidate.Body is { } body)
					request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

				using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				abort.CancelAfter(timeout);
				string raw;
				try
				{
					using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token)
						.ConfigureAwait(false);
					if (!response.IsSuccessStatusCode)
					{
						var summary = await ReadBoundedAsync(response, abort.Token).ConfigureAwait(false);
						attempts.Add(new(url, candidate.Method, (int)response.StatusCode,
							string.IsNullOrWhiteSpace(summary) ? null : Redact(summary, plan.Token)));
						continue;
					}
					raw = await ReadBoundedAsync(response, abort.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch (OperationCanceledException)
				{
					attempts.Add(new(url, candidate.Method, Error: "timeout"));
					continue;
				}
				catch (InvalidOperationException ex) when (ex.Message == Strings.UsageResponseTooLarge)
				{
					attempts.Add(new(url, candidate.Method, Error: "large"));
					continue;
				}
				catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
				{
					attempts.Add(new(url, candidate.Method, Error: "network"));
					continue;
				}

				JsonNode? node;
				try { node = JsonNode.Parse(raw); }
				catch (System.Text.Json.JsonException) { node = null; }
				var parsed = UsageProbes.Parse(node, candidate.Parse);
				if (!parsed.Matched)
				{
					attempts.Add(new(url, candidate.Method, Shape: true));
					continue;
				}
				return new ProviderUsageResult(true, parsed.Kind, parsed.Periods, parsed.Balance, parsed.Currency,
					parsed.Credits, parsed.Booster, At: startedAt, IntervalMinutes: intervalMinutes);
			}
		}

		if (attempts.Count == 0) return ProviderUsageResult.Failure(Strings.UsageUnavailable);
		if (attempts.All(attempt => attempt.Error == "large")) return ProviderUsageResult.Failure(Strings.UsageResponseTooLarge);
		// 全部失败且原因都是「没凭据」时给专属文案，不把用户引向接口排查。
		if (missingKey && attempts.All(attempt => attempt.Error == Strings.UsageMissingKey))
			return ProviderUsageResult.Failure(Strings.UsageMissingKey);
		return ProviderUsageResult.Failure(Strings.UsageUnavailable, BuildDetail(attempts, plan.Token));
	}

	private async Task<string?> RunPreflightAsync(Model model, UsageProbePlan plan, UsageProbePreflight preflight, TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		var baseUri = Uri.TryCreate(plan.BaseUrl, UriKind.Absolute, out var parsedBase) ? parsedBase : null;
		var modelOrigin = Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelBase)
			? modelBase.GetLeftPart(UriPartial.Authority) : string.Empty;
		foreach (var url in UsageProbes.Urls(new UsageProbeCandidate(preflight.Path, preflight.AbsoluteUrl), plan.BaseUrl))
		{
			if (!TryValidateUrl(url, baseUri, preflight.AbsoluteUrl.Length > 0, out var uri)) continue;
			var sameOrigin = string.Equals(uri.GetLeftPart(UriPartial.Authority), modelOrigin, StringComparison.OrdinalIgnoreCase);
			var headers = UsageProbes.BuildHeaders(new UsageProbeCandidate(preflight.Path, Headers: preflight.Headers),
				plan.Token, sameOrigin ? model.Headers : null, sameOrigin);
			using var request = new HttpRequestMessage(HttpMethod.Get, uri);
			foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
			using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			abort.CancelAfter(timeout);
			try
			{
				using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token)
					.ConfigureAwait(false);
				if (!response.IsSuccessStatusCode) continue;
				var raw = await ReadBoundedAsync(response, abort.Token).ConfigureAwait(false);
				var captured = UsageJson.TextAt(JsonNode.Parse(raw), preflight.CapturePath);
				if (!string.IsNullOrWhiteSpace(captured)) return captured;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or
				System.Text.Json.JsonException or ArgumentException) { }
		}
		return null;
	}

	/// <summary>URL 安全边界：仅 http(s)、无 userinfo、HTTP 仅限本机；跨域必须来自代码内置的 absoluteUrl。</summary>
	private static bool TryValidateUrl(string url, Uri? baseUri, bool builtinAbsolute, out Uri uri)
	{
		uri = null!;
		if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
		if (parsed.Scheme is not ("https" or "http") || parsed.UserInfo.Length > 0) return false;
		if (parsed.Scheme == "http" && !parsed.IsLoopback) return false;
		if (baseUri is not null && !string.Equals(parsed.GetLeftPart(UriPartial.Authority),
			baseUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) && !builtinAbsolute) return false;
		uri = parsed;
		return true;
	}

	private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidOperationException(Strings.UsageResponseTooLarge);
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		var bytes = new byte[8192];
		int count;
		while ((count = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + count > MaxResponseBytes) throw new InvalidOperationException(Strings.UsageResponseTooLarge);
			buffer.Write(bytes, 0, count);
		}
		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	public const int MaxResponseBytes = UsageProbes.MaxResponseBytes;

	private static string Redact(string value, string secret) =>
		secret.Length == 0 ? value : value.Replace(secret, "***", StringComparison.Ordinal);

	/// <summary>失败明细：每次尝试一行（方法 + URL + 状态/错误 + 响应摘要），末尾给归纳提示。</summary>
	public static string BuildDetail(IReadOnlyList<UsageProbeAttempt> attempts, string token)
	{
		var lines = new List<string> { Strings.UsageAttemptsTitle };
		foreach (var attempt in attempts)
		{
			if (attempt.Status is { } status)
			{
				lines.Add($"{attempt.Method} {attempt.Url} → HTTP {status}");
				if (attempt.Body is { Length: > 0 } body)
					lines.Add("  " + Redact(Compact(body), token));
			}
			else if (attempt.Shape) lines.Add($"{attempt.Method} {attempt.Url} → {Strings.UsageAttemptShape}");
			else if (attempt.Error is { } error)
				lines.Add($"{attempt.Method} {attempt.Url} → {(error == Strings.UsageMissingKey ? error : error == "large" ? Strings.UsageResponseTooLarge : error == "timeout" ? Strings.UsageAttemptTimeout : Strings.UsageAttemptNetwork)}");
		}
		var hint = Hint(attempts);
		if (hint.Length > 0) { lines.Add(""); lines.Add(Strings.UsageHintTitle + hint); }
		return string.Join("\n", lines);
	}

	private static string Compact(string value)
	{
		var builder = new StringBuilder(Math.Min(value.Length, 240));
		foreach (var c in value)
		{
			if (builder.Length >= 240) break;
			builder.Append(char.IsControl(c) ? ' ' : c);
		}
		return builder.ToString().Trim();
	}

	private static string Hint(IReadOnlyList<UsageProbeAttempt> attempts)
	{
		if (attempts.All(attempt => attempt.Error == Strings.UsageMissingKey)) return Strings.UsageHintAuth;
		if (attempts.All(attempt => attempt.Shape)) return Strings.UsageHintShape;
		if (attempts.All(attempt => attempt.Status is 401 or 403)) return Strings.UsageHintAuth;
		if (attempts.All(attempt => attempt.Status == 404)) return Strings.UsageHintNotFound;
		if (attempts.Any(attempt => attempt.Status >= 500)) return Strings.UsageHintServer;
		if (attempts.All(attempt => attempt.Error is "timeout" or "network")) return Strings.UsageHintTimeout;
		return Strings.UsageHintMixed;
	}

	public static string? ResolveKey(Model model, string cwd)
	{
		// Codex 的凭据不走 OAuthStore 的刷新链：用 CodexAuth.ResolveAsync 才能在令牌过期时续期，
		// 否则用量查询会拿着旧 access token 得到 401。
		if (model.Provider == CodexAuth.ProviderKey)
		{
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
				return CodexAuth.ResolveAsync(CodexAuthClient, cancellationToken: timeout.Token)
					.GetAwaiter().GetResult().Access;
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException or
				OperationCanceledException or IOException)
			{
				// 续期失败时回落到已存凭据；查询本身会给出 401 明细。
			}
		}
		return ProviderEnvironment.ResolveApiKey(model.Provider,
			new StreamOptions { ApiKey = ModelsFileLoader.Load(cwd).ApiKeys.GetValueOrDefault(model.Provider) });
	}

	private static readonly HttpClient CodexAuthClient = new() { Timeout = TimeSpan.FromSeconds(30) };
}
