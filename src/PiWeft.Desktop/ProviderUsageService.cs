using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

internal sealed record UsageProbePlan(string BaseUrl, string Token, IReadOnlyList<UsageProbeCandidate> Candidates, string? Error = null);

internal sealed record UsageProbeAttempt(string Url, string Method, int? Status = null, string? Body = null, bool Shape = false, string? Error = null);

/// 预检结果：失败时也带上 URL、状态和错误，让排查明细能看到 xAI 这类两步探针卡在哪一步。
internal sealed record UsagePreflightOutcome(string Url, string? Capture = null, int? Status = null, bool Shape = false,
	string? Error = null);

/// 供应商用量/余额探测：逐候选、逐 URL 尝试，首个解析成功的结果即返回。
/// 凭据只在主进程发请求；响应原文不落日志、不回传 UI，只保留脱敏截断后的尝试明细。
internal sealed class ProviderUsageService(HttpClient client)
{
	// 总超时由每次探测的 CancellationTokenSource 控制（按 provider 配置 1-300 秒），
	// HttpClient 自身不设总时限，否则配置成 30 秒也会被 15 秒截断。
	public static readonly HttpClient Client = new(new HttpClientHandler
	{
		AllowAutoRedirect = false,
		// 用量请求无状态：不要复用别的供应商响应留下的 Cookie，也不要为了压缩包手动加解压层。
		UseCookies = false,
		AutomaticDecompression = DecompressionMethods.All,
	})
	{
		Timeout = Timeout.InfiniteTimeSpan,
	};

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

	public static async Task<ProviderUsageResult> QueryAsync(Model model, UsageProbeProviderConfig config, UsageProbeDocument document,
		UsageProbeCandidate? legacy, string cwd, CancellationToken cancellationToken, HttpClient? client = null)
	{
		var plan = await BuildPlanAsync(model, config, document, legacy, cwd, cancellationToken).ConfigureAwait(false);
		if (plan.Error is { } error) return ProviderUsageResult.Failure(error, intervalMinutes: config.IntervalMinutes);
		var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSecs, 1, 300));
		return await new ProviderUsageService(client ?? Client)
			.ProbeAsync(model, config, plan, timeout, config.IntervalMinutes, cancellationToken)
			.ConfigureAwait(false);
	}

	public static async Task<ProviderUsageResult> TestAsync(Model model, UsageProbeProviderConfig config, string cwd,
		CancellationToken cancellationToken)
	{
		var agentDir = SessionPaths.GetAgentDir();
		var document = UsageProbeStore.Load(agentDir);
		var legacy = UsageProbeStore.LoadLegacyCandidate(agentDir, model.Provider);
		return await QueryAsync(model, config, document, legacy, cwd, cancellationToken).ConfigureAwait(false);
	}

	public static async Task<UsageProbePlan> BuildPlanAsync(Model model, UsageProbeProviderConfig config, UsageProbeDocument document,
		UsageProbeCandidate? legacy, string cwd, CancellationToken cancellationToken)
	{
		var baseUrl = model.BaseUrl.Trim();
		if (baseUrl.Length == 0) return new("", "", [], Strings.UsageNoBaseUrl);
		var token = config.ApiKey.Trim();
		if (token.Length == 0)
			token = await ResolveKeyAsync(model, cwd, cancellationToken).ConfigureAwait(false) ?? string.Empty;

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

	public async Task<ProviderUsageResult> ProbeAsync(Model model, UsageProbeProviderConfig config, UsageProbePlan plan,
		TimeSpan timeout, int intervalMinutes, CancellationToken cancellationToken)
	{
		var attempts = new List<UsageProbeAttempt>();
		var missingKey = false;
		var startedAt = DateTimeOffset.UtcNow;
		cancellationToken.ThrowIfCancellationRequested();
		var baseUri = Uri.TryCreate(plan.BaseUrl, UriKind.Absolute, out var parsedBase) ? parsedBase : null;
		// 模型自定义头只回发给供应商自己的域：模板允许把用量端点覆盖到别的域，
		// 但那些自定义头（可能含鉴权/租户标）不应跟着过去。
		var modelOrigin = Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelBase)
			? modelBase.GetLeftPart(UriPartial.Authority) : string.Empty;
		// 配置里的超时是对整次探测的预算，不是每条候选 URL 各给一份。
		// 否则多候选供应商会把一次查询拖成 N 倍超时，统计面板没有外层兜底时会一直转圈。
		using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probe.CancelAfter(timeout);

		foreach (var candidate in plan.Candidates)
		{
			if (probe.IsCancellationRequested) break;
			// 链式预检（xAI 先查 identity 拿 userId）：失败则整个候选跳过，并把失败写进明细。
			Dictionary<string, string> preflightHeaders = new(StringComparer.OrdinalIgnoreCase);
			if (candidate.Preflight is { } preflight)
			{
				var outcome = await RunPreflightAsync(model, plan, preflight, cancellationToken, probe.Token).ConfigureAwait(false);
				if (outcome.Capture is null)
				{
					attempts.Add(new(outcome.Url, "GET", outcome.Status, Shape: outcome.Shape, Error: outcome.Error));
					if (probe.IsCancellationRequested) break;
					continue;
				}
				preflightHeaders[preflight.CaptureHeader] = outcome.Capture;
			}

			foreach (var url in UsageProbes.Urls(candidate, plan.BaseUrl))
			{
				if (probe.IsCancellationRequested) break;
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

				string raw;
				try
				{
					using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probe.Token)
						.ConfigureAwait(false);
					if (!response.IsSuccessStatusCode)
					{
						var summary = await ReadBoundedAsync(response, probe.Token).ConfigureAwait(false);
						attempts.Add(new(url, candidate.Method, (int)response.StatusCode,
							string.IsNullOrWhiteSpace(summary) ? null : Redact(summary, plan.Token)));
						continue;
					}
					raw = await ReadBoundedAsync(response, probe.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch (OperationCanceledException)
				{
					attempts.Add(new(url, candidate.Method, Error: "timeout"));
					break;
				}
				catch (UsageResponseTooLargeException)
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
				var result = new ProviderUsageResult(true, parsed.Kind, parsed.Periods, parsed.Balance, parsed.Currency,
					parsed.Credits, parsed.Booster, At: startedAt, IntervalMinutes: intervalMinutes);
				return await WithPanelBalanceAsync(model, config, result, cancellationToken).ConfigureAwait(false);
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		if (attempts.Count == 0) return ProviderUsageResult.Failure(Strings.UsageUnavailable, intervalMinutes: intervalMinutes);
		if (attempts.All(attempt => attempt.Error == "large"))
			return ProviderUsageResult.Failure(Strings.UsageResponseTooLarge, intervalMinutes: intervalMinutes);
		// 全部失败且原因都是「没凭据」时给专属文案，不把用户引向接口排查。
		if (missingKey && attempts.All(attempt => attempt.Error == Strings.UsageMissingKey))
			return ProviderUsageResult.Failure(Strings.UsageMissingKey, intervalMinutes: intervalMinutes);
		return ProviderUsageResult.Failure(Strings.UsageUnavailable, BuildDetail(attempts, plan.Token), intervalMinutes);
	}

	/// Sub2API 的余额段：网关 apiKey 的 `/v1/usage` 不含面板钱包，PiDeck 另用网页后台 JWT
	/// 查 `GET /api/v1/auth/me`。主结果已经有余额时不覆盖；面板任何失败都只让这一段缺席。
	private async Task<ProviderUsageResult> WithPanelBalanceAsync(
		Model model,
		UsageProbeProviderConfig config,
		ProviderUsageResult result,
		CancellationToken cancellationToken)
	{
		if (result.Balance is not null ||
			!string.Equals(config.Template, "sub2api", StringComparison.Ordinal) ||
			config.PanelJwt.Length == 0)
		{
			return result;
		}

		var baseUrl = config.BaseUrl.Length > 0 ? config.BaseUrl : model.BaseUrl;
		var timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSecs, 1, 300));
		var (balance, rotated) = await Sub2ApiPanel
			.FetchBalanceAsync(client, baseUrl, new Sub2ApiPanel.Tokens(config.PanelJwt, config.PanelRefreshToken), timeout,
				DateTimeOffset.UtcNow, cancellationToken)
			.ConfigureAwait(false);

		if (rotated is not null)
		{
			// 上游轮转 refresh token：不立刻回写，下一次过期就彻底续不上。
			UsageProbeStore.UpdatePanelTokens(SessionPaths.GetAgentDir(), model.Provider, rotated.AccessToken, rotated.RefreshToken);
		}

		return balance is null
			? result
			: result with { Balance = balance.Value, Currency = balance.Currency ?? result.Currency };
	}

	private async Task<UsagePreflightOutcome> RunPreflightAsync(Model model, UsageProbePlan plan, UsageProbePreflight preflight,
		CancellationToken cancellationToken, CancellationToken probeToken)
	{
		var baseUri = Uri.TryCreate(plan.BaseUrl, UriKind.Absolute, out var parsedBase) ? parsedBase : null;
		var modelOrigin = Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var modelBase)
			? modelBase.GetLeftPart(UriPartial.Authority) : string.Empty;
		var last = new UsagePreflightOutcome(preflight.Path);
		foreach (var url in UsageProbes.Urls(new UsageProbeCandidate(preflight.Path, preflight.AbsoluteUrl), plan.BaseUrl))
		{
			last = new UsagePreflightOutcome(url);
			if (!TryValidateUrl(url, baseUri, preflight.AbsoluteUrl.Length > 0, out var uri))
			{
				last = last with { Error = Strings.UsageInvalidConfig };
				continue;
			}
			var sameOrigin = string.Equals(uri.GetLeftPart(UriPartial.Authority), modelOrigin, StringComparison.OrdinalIgnoreCase);
			var headers = UsageProbes.BuildHeaders(new UsageProbeCandidate(preflight.Path, Headers: preflight.Headers),
				plan.Token, sameOrigin ? model.Headers : null, sameOrigin);
			using var request = new HttpRequestMessage(HttpMethod.Get, uri);
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
			try
			{
				using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeToken)
					.ConfigureAwait(false);
				if (!response.IsSuccessStatusCode) return last with { Status = (int)response.StatusCode };
				var raw = await ReadBoundedAsync(response, probeToken).ConfigureAwait(false);
				var captured = UsageJson.TextAt(JsonNode.Parse(raw), preflight.CapturePath);
				return string.IsNullOrWhiteSpace(captured) ? last with { Shape = true } : last with { Capture = captured };
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (OperationCanceledException) { return last with { Error = "timeout" }; }
			catch (UsageResponseTooLargeException) { return last with { Error = "large" }; }
			catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException) { return last with { Shape = true }; }
			catch (Exception ex) when (ex is HttpRequestException or IOException) { return last with { Error = "network" }; }
		}
		return last;
	}

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
		if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new UsageResponseTooLargeException();
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		// 上限 1 MiB，按需扩容并从 ArrayPool 借缓冲区：一次解析只留一个 string，
		// 避免 MemoryStream 增长、ToArray 拷贝和每请求一个 8 KiB 数组。
		var buffer = ArrayPool<byte>.Shared.Rent(8192);
		try
		{
			var length = 0;
			while (true)
			{
				if (length == buffer.Length)
				{
					var next = ArrayPool<byte>.Shared.Rent((int)Math.Min(buffer.Length * 2L, MaxResponseBytes + 1L));
					buffer.AsSpan(0, length).CopyTo(next);
					ArrayPool<byte>.Shared.Return(buffer);
					buffer = next;
				}
				var space = Math.Min(buffer.Length, MaxResponseBytes + 1) - length;
				var count = await stream.ReadAsync(buffer.AsMemory(length, space), cancellationToken).ConfigureAwait(false);
				if (count == 0) break;
				length += count;
				if (length > MaxResponseBytes) throw new UsageResponseTooLargeException();
			}
			return Encoding.UTF8.GetString(buffer, 0, length);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private sealed class UsageResponseTooLargeException : InvalidOperationException
	{
		public UsageResponseTooLargeException() : base(Strings.UsageResponseTooLarge) { }
	}

	public const int MaxResponseBytes = UsageProbes.MaxResponseBytes;

	private static string Redact(string value, string secret) =>
		secret.Length == 0 ? value : value.Replace(secret, "***", StringComparison.Ordinal);

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
			{
				var reason = error == Strings.UsageMissingKey ? error
					: error == Strings.UsageInvalidConfig ? error
					: error == "large" ? Strings.UsageResponseTooLarge
					: error == "timeout" ? Strings.UsageAttemptTimeout
					: Strings.UsageAttemptNetwork;
				lines.Add($"{attempt.Method} {attempt.Url} → {reason}");
			}
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

	public static async Task<string?> ResolveKeyAsync(Model model, string cwd, CancellationToken cancellationToken)
	{
		// Codex 的凭据不走 OAuthStore 的刷新链：用 CodexAuth.ResolveAsync 才能在令牌过期时续期，
		// 否则用量查询会拿着旧 access token 得到 401。
		if (model.Provider == CodexAuth.ProviderKey)
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			try
			{
				return (await CodexAuth.ResolveAsync(CodexAuthClient, cancellationToken: timeout.Token).ConfigureAwait(false)).Access;
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				// 续期超时：回落到已存凭据；查询本身会给出 401 明细。
			}
			catch (Exception ex) when (ex is CodexAuthException or HttpRequestException or System.Text.Json.JsonException or IOException)
			{
				// 续期失败时回落到已存凭据；查询本身会给出 401 明细。
			}
		}
		return await ProviderEnvironment.ResolveApiKeyAsync(model.Provider,
			new StreamOptions { ApiKey = ModelsFileLoader.Load(cwd).ApiKeys.GetValueOrDefault(model.Provider) },
			cancellationToken).ConfigureAwait(false);
	}

	private static readonly HttpClient CodexAuthClient = new() { Timeout = TimeSpan.FromSeconds(30) };
}
