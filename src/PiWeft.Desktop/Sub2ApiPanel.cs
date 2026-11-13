using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

/// Sub2API 面板登录态（网页后台 JWT）的余额查询与续期。PiDeck 0.12 的同名能力：
/// 网关 apiKey 走 `/v1/usage` 拿主用量，面板 JWT 追加一条钱包余额（`GET /api/v1/auth/me`）。
/// <para>
/// refresh token 在上游是单次轮转的（用过即失效），所以只在两种信号下续期：
/// 本地 JWT 的 `exp` 已到（提前 60 秒视作过期），或 `/auth/me` 真的返回 401。
/// 续期成功后必须立刻回写，否则下一次过期就彻底续不上。
/// </para>
/// <para>
/// 余额段是补充信息：任何失败都只让这一段缺席，不影响主用量结果。
/// </para>
internal static partial class Sub2ApiPanel
{
	/// 提前量：传输 + 服务端处理有耗时，掐着 exp 发请求会稳定换来一次 401。
	public static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

	public const int MaxResponseBytes = 1024 * 1024;

	public sealed record Tokens(string AccessToken, string? RefreshToken);

	public sealed record Refreshed(string AccessToken, string? RefreshToken);

	public sealed record Balance(double Value, string? Currency);

	/// 取 JWT 的 exp（毫秒时间戳）。只认 `"exp":<数字>` 字面量，不做整段 JSON 解析：
	/// payload 常含非 ASCII 邮箱，整段解码容易在编码边界失败，而这里只需要一个数字。
	public static long? DecodeJwtExpiry(string token)
	{
		var parts = token.Split('.');
		if (parts.Length < 2 || parts[1].Length == 0)
		{
			return null;
		}

		string payload;
		try
		{
			var bytes = Base64UrlDecode(parts[1]);
			payload = Encoding.Latin1.GetString(bytes);
		}
		catch (FormatException)
		{
			return null;
		}

		var match = ExpiryPattern().Match(payload);
		if (!match.Success ||
			!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
			seconds <= 0)
		{
			return null;
		}

		return seconds * 1000;
	}

	/// 没有 exp 的令牌不下判断，交给服务端 401 兜底。
	public static bool NeedsRefresh(string accessToken, DateTimeOffset now)
	{
		var expiry = DecodeJwtExpiry(accessToken);
		return expiry is { } value && value - (long)RefreshSkew.TotalMilliseconds <= now.ToUnixTimeMilliseconds();
	}

	/// 续期响应：上游走 `{code,message,data}` 包一层，个别反代直接给扁平结构，两处都接受。
	public static Refreshed? ParseRefresh(JsonNode? body)
	{
		var root = body as JsonObject;
		var data = root?["data"] as JsonObject ?? root;
		if (data is null)
		{
			return null;
		}

		var accessToken = Text(data, "access_token");
		if (accessToken.Length == 0)
		{
			return null;
		}

		var refreshToken = Text(data, "refresh_token");
		return new Refreshed(accessToken, refreshToken.Length > 0 ? refreshToken : null);
	}

	/// `/auth/me` 的余额：`data.balance`（个别版本在顶层），单位 `data.unit` / `data.currency`。
	public static Balance? ParseBalance(JsonNode? body)
	{
		var root = body as JsonObject;
		var data = root?["data"] as JsonObject ?? root;
		if (data is null)
		{
			return null;
		}

		var value = UsageJson.Number(data["balance"]) ?? UsageJson.Number(root?["balance"]);
		if (value is null)
		{
			return null;
		}

		var currency = Text(data, "unit");
		if (currency.Length == 0)
		{
			currency = Text(data, "currency");
		}

		return new Balance(value.Value, currency.Length > 0 ? currency : null);
	}

	/// 查余额，必要时续期一次。返回的 <see cref="Refreshed"/> 非空表示 token 被轮转过，调用方要回写。
	public static async Task<(Balance? Balance, Refreshed? Rotated)> FetchBalanceAsync(
		HttpClient client,
		string baseUrl,
		Tokens tokens,
		TimeSpan timeout,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		// 与用量探测同一套安全边界：只走 HTTPS 或本机 HTTP，令牌不能明文发到远端。
		if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
			origin.Scheme is not ("https" or "http") ||
			(origin.Scheme == "http" && !origin.IsLoopback))
		{
			return (null, null);
		}

		var root = origin.GetLeftPart(UriPartial.Authority);
		var current = tokens;
		Refreshed? rotated = null;
		// refresh token 单次轮转：同一次查询里只许续一次。续期失败后再拿同一个 token 重试，
		// 服务端可能已经把它用掉了，重试只会把两边都搞成不可用。
		var refreshAttempted = false;
		using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		budget.CancelAfter(timeout);

		if (current.RefreshToken is { Length: > 0 } && NeedsRefresh(current.AccessToken, now))
		{
			refreshAttempted = true;
			rotated = await RefreshAsync(client, root, current.RefreshToken, budget.Token).ConfigureAwait(false);
			if (rotated is not null)
			{
				current = new Tokens(rotated.AccessToken, rotated.RefreshToken ?? current.RefreshToken);
			}
		}

		var (balance, unauthorized) = await ReadBalanceAsync(client, root, current.AccessToken, budget.Token).ConfigureAwait(false);
		if (balance is null && unauthorized && !refreshAttempted && current.RefreshToken is { Length: > 0 })
		{
			// 本地 exp 没到但服务端提前失效（时钟偏差/被撤销）：续一次再重试同一个端点。
			rotated = await RefreshAsync(client, root, current.RefreshToken, budget.Token).ConfigureAwait(false);
			if (rotated is not null)
			{
				current = new Tokens(rotated.AccessToken, rotated.RefreshToken ?? current.RefreshToken);
				(balance, _) = await ReadBalanceAsync(client, root, current.AccessToken, budget.Token).ConfigureAwait(false);
			}
		}

		return (balance, rotated);
	}

	private static async Task<(Balance? Balance, bool Unauthorized)> ReadBalanceAsync(
		HttpClient client,
		string root,
		string accessToken,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, root + "/api/v1/auth/me");
		request.Headers.Accept.ParseAdd("application/json");
		request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
		try
		{
			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
			{
				return (null, true);
			}

			if (!response.IsSuccessStatusCode)
			{
				return (null, false);
			}

			var raw = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
			return (ParseBalance(JsonNode.Parse(raw)), false);
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
		{
			return (null, false);
		}
	}

	private static async Task<Refreshed?> RefreshAsync(HttpClient client, string root, string refreshToken, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, root + "/api/v1/auth/refresh")
		{
			Content = new StringContent(
				new JsonObject { ["refresh_token"] = refreshToken }.ToJsonString(),
				Encoding.UTF8,
				"application/json"),
		};
		try
		{
			using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			var raw = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
			return ParseRefresh(JsonNode.Parse(raw));
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException)
		{
			return null;
		}
	}

	private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var buffer = new MemoryStream();
		var chunk = new byte[16 * 1024];
		while (buffer.Length < MaxResponseBytes)
		{
			var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxResponseBytes - (int)buffer.Length)), cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			buffer.Write(chunk, 0, read);
		}

		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	private static byte[] Base64UrlDecode(string value)
	{
		var normalized = value.Replace('-', '+').Replace('_', '/');
		var padding = normalized.Length % 4;
		if (padding > 0)
		{
			normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
		}

		return Convert.FromBase64String(normalized);
	}

	private static string Text(JsonObject item, string key) =>
		item[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : string.Empty;

	[System.Text.RegularExpressions.GeneratedRegex("\"exp\"\\s*:\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex ExpiryPattern();
}
