using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.Desktop;

internal sealed record UsageQuerySpec(string Path, string ValuePath, string CurrencyPath = "", string Currency = "",
	string TotalPath = "", string UsedPath = "", decimal Scale = 1, bool RootPath = false,
	string Token = "", string Cookie = "", string UserId = "");
internal sealed record ProviderBalance(decimal? Remaining, decimal? Total, decimal? Used, string Currency)
{
	public string Display() => string.Join(" · ", new[]
	{
		Remaining is { } remaining ? $"{Strings.UsageRemaining} {remaining:0.####} {Currency}" : null,
		Total is { } total ? $"{Strings.UsageTotal} {total:0.####} {Currency}" : null,
		Used is { } used ? $"{Strings.UsageUsed} {used:0.####} {Currency}" : null,
	}.Where(value => value is not null));
}

/// <summary>Read-only balance requests. Redirects never receive credentials; responses and errors are not logged.</summary>
internal sealed class ProviderUsageService(HttpClient client)
{
	public const int MaxResponseBytes = 1024 * 1024;
	public static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

	public static UsageQuerySpec BuiltIn(Model model)
	{
		var host = new Uri(model.BaseUrl).Host.ToLowerInvariant();
		return host switch
		{
			"api.deepseek.com" => new("/user/balance", "balance_infos[0].total_balance", "balance_infos[0].currency", RootPath: true),
			"openrouter.ai" => new("/key", "data.limit_remaining", Currency: "USD", TotalPath: "data.limit", UsedPath: "data.usage"),
			"api.moonshot.cn" => new("/users/me/balance", "data.available_balance", Currency: "CNY", RootPath: true),
			"api.moonshot.ai" => new("/users/me/balance", "data.available_balance", Currency: "USD", RootPath: true),
			"api.siliconflow.cn" => new("/user/info", "data.totalBalance", Currency: "CNY"),
			"api.siliconflow.com" => new("/user/info", "data.totalBalance", Currency: "USD"),
			_ => new("/usage", "balance", "unit"),
		};
	}

	public static UsageQuerySpec NewApi(string token, string userId) =>
		new("/api/user/self", "data.quota", Currency: "USD", UsedPath: "data.used_quota", Scale: 500000, RootPath: true, Token: token, UserId: userId);

	public static UsageQuerySpec ReadSpec(Model model, JsonObject root)
	{
		if (root["providers"]?[model.Provider] is not JsonObject entry) return BuiltIn(model);
		var spec = new UsageQuerySpec(ConfigurationModels.Text(entry, "path"), ConfigurationModels.Text(entry, "valuePath"),
			ConfigurationModels.Text(entry, "currencyPath"), ConfigurationModels.Text(entry, "currency"),
			ConfigurationModels.Text(entry, "totalPath"), ConfigurationModels.Text(entry, "usedPath"),
			Number(entry["scale"]) ?? 1, ConfigurationModels.Flag(entry, "rootPath"),
			ConfigurationModels.Text(entry, "token"), ConfigurationModels.Text(entry, "cookie"), ConfigurationModels.Text(entry, "userId"));
		Validate(model, spec);
		return spec;
	}

	public static JsonObject SaveSpec(JsonObject root, string provider, UsageQuerySpec? spec)
	{
		var next = root.DeepClone().AsObject();
		var providers = next["providers"] as JsonObject ?? new JsonObject();
		if (next["providers"] is null) next["providers"] = providers;
		if (spec is null) providers.Remove(provider);
		else providers[provider] = new JsonObject
		{
			["path"] = spec.Path, ["valuePath"] = spec.ValuePath, ["currencyPath"] = spec.CurrencyPath,
			["currency"] = spec.Currency, ["totalPath"] = spec.TotalPath, ["usedPath"] = spec.UsedPath,
			["scale"] = spec.Scale, ["rootPath"] = spec.RootPath, ["token"] = spec.Token, ["cookie"] = spec.Cookie, ["userId"] = spec.UserId,
		};
		return next;
	}

	public static Uri Endpoint(Model model, UsageQuerySpec spec)
	{
		var baseUri = new Uri(model.BaseUrl.TrimEnd('/') + "/");
		if (Uri.TryCreate(spec.Path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute;
		return new Uri(spec.RootPath ? new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/") : baseUri, spec.Path.TrimStart('/'));
	}

	public static void Validate(Model model, UsageQuerySpec spec)
	{
		if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http"))
			throw new ArgumentException(Strings.UsageInvalidConfig);
		if (string.IsNullOrWhiteSpace(spec.Path) || spec.Path.Any(char.IsControl) || spec.Scale <= 0 ||
			string.IsNullOrWhiteSpace(spec.ValuePath) && string.IsNullOrWhiteSpace(spec.TotalPath) && string.IsNullOrWhiteSpace(spec.UsedPath))
			throw new ArgumentException(Strings.UsageInvalidConfig);
		var endpoint = Endpoint(model, spec);
		if (endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 ||
			endpoint.Scheme == "http" && !endpoint.IsLoopback ||
			(endpoint.Authority != baseUri.Authority || endpoint.Scheme != baseUri.Scheme) && spec.Token.Length == 0 && spec.Cookie.Length == 0)
			throw new ArgumentException(Strings.UsageInvalidConfig);
		if (new[] { spec.Token, spec.Cookie, spec.UserId }.Any(value => value.Any(char.IsControl))) throw new ArgumentException(Strings.UsageInvalidConfig);
	}

	public async Task<ProviderBalance> QueryAsync(Model model, UsageQuerySpec spec, string? key, CancellationToken cancellationToken)
	{
		Validate(model, spec);
		using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(model, spec));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		var sameOrigin = request.RequestUri!.GetLeftPart(UriPartial.Authority) == new Uri(model.BaseUrl).GetLeftPart(UriPartial.Authority);
		if (sameOrigin && spec.Token.Length == 0 && spec.Cookie.Length == 0 && model.Headers is { } headers)
			foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
		if (spec.Cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", spec.Cookie);
		else
		{
			var token = spec.Token.Length > 0 ? spec.Token : sameOrigin ? key : null;
			if (!string.IsNullOrEmpty(token) && !request.Headers.Contains("Authorization"))
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			if (string.IsNullOrEmpty(token) && !request.Headers.Contains("Authorization")) throw new InvalidOperationException(Strings.UsageMissingKey);
		}
		if (spec.UserId.Length > 0) request.Headers.TryAddWithoutValidation("New-Api-User", spec.UserId);
		using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Strings.UsageHttpError((int)response.StatusCode));
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
		return Parse(JsonNode.Parse(buffer.ToArray()), spec);
	}

	public static ProviderBalance Parse(JsonNode? root, UsageQuerySpec spec)
	{
		if (root is JsonObject obj && obj["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
			throw new InvalidOperationException(Strings.UsageUnavailable);
		var remaining = Number(At(root, spec.ValuePath)) / spec.Scale;
		var total = Number(At(root, spec.TotalPath)) / spec.Scale;
		var used = Number(At(root, spec.UsedPath)) / spec.Scale;
		remaining ??= total - used;
		if (remaining is null && total is null && used is null) throw new InvalidOperationException(Strings.UsageUnavailable);
		var currency = At(root, spec.CurrencyPath) is JsonValue value && value.TryGetValue<string>(out var text) ? text : spec.Currency;
		// Only short currency labels reach the UI, never arbitrary raw response text.
		if (currency.Length > 12 || currency.Any(char.IsControl)) currency = string.Empty;
		return new(remaining, total, used, currency);
	}

	public static JsonNode? At(JsonNode? node, string path)
	{
		if (path.Length == 0) return null;
		foreach (var segment in path.Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
		{
			if (node is JsonObject obj) node = obj[segment];
			else if (node is JsonArray array && int.TryParse(segment, out var index) && index >= 0 && index < array.Count) node = array[index];
			else return null;
		}
		return node;
	}

	private static decimal? Number(JsonNode? node)
	{
		if (node is not JsonValue value) return null;
		if (value.TryGetValue<decimal>(out var number)) return number;
		return value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
	}

	public static string? ResolveKey(Model model, string cwd) =>
		ProviderEnvironment.ResolveApiKey(model.Provider, new StreamOptions { ApiKey = ModelsFileLoader.Load(cwd).ApiKeys.GetValueOrDefault(model.Provider) });
}
