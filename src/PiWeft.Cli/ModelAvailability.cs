using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.OpenAi;

namespace PiWeft.Cli;

internal static class ModelAvailability
{
	public static IReadOnlyList<Model> Filter(IEnumerable<Model> models,
		IReadOnlyDictionary<string, string> configuredKeys, string? explicitKey = null)
	{
		var stored = AuthStore.ReadApiKeys();
		// 命令型凭据按可用处理（与 pi 一致：命令在请求时执行）；这里只查文件，不在启动路径上跑进程。
		var commandProviders = new HashSet<string>(AuthStore.CommandCredentialProviders(), StringComparer.Ordinal);
		var credentials = ReadCodexCredentials();
		var providers = new Dictionary<(string Provider, string Api), bool>();
		var oauthProviders = new Dictionary<string, bool>(StringComparer.Ordinal);
		return [.. models.Where(model =>
		{
			if (!CliEnvironment.SupportsApi(model.Api)) return false;
			// 虚拟模型本身不需要凭据；它的路由目标会在请求时由 provider 校验。
			if (VirtualModels.IsVirtual(model)) return true;
			var identity = (model.Provider, model.Api);
			if (!providers.TryGetValue(identity, out var available))
			{
				var configuredKey = configuredKeys.GetValueOrDefault(model.Provider);
				var key = explicitKey is { Length: > 0 } ? explicitKey : configuredKey is { Length: > 0 } ? configuredKey :
					stored.GetValueOrDefault(model.Provider) ?? ProviderEnvironment.ResolveEnvironmentKey(model.Provider);
				var hasKey = !string.IsNullOrWhiteSpace(key) && !key.StartsWith('!');
				var hasCommandKey = commandProviders.Contains(model.Provider);
				if (!oauthProviders.TryGetValue(model.Provider, out var hasOAuth))
				{
					hasOAuth = OAuthStore.Read(model.Provider) is not null;
					oauthProviders[model.Provider] = hasOAuth;
				}
				available = model.Api switch
				{
					OpenAiCodexResponsesProvider.ApiId => !string.IsNullOrWhiteSpace(explicitKey) ||
						configuredKeys.GetValueOrDefault(model.Provider) is { Length: > 0 } configured && !configured.StartsWith('!') ||
						credentials is { Access.Length: > 0, Refresh.Length: > 0 },
					BedrockConverseProvider.ApiId => AwsCredentials.FromEnvironment(model.Provider).IsValid,
					GoogleVertexProvider.ApiId => hasKey || hasCommandKey || !string.IsNullOrWhiteSpace(explicitKey) ||
						configuredKeys.GetValueOrDefault(model.Provider) is { Length: > 0 } vertexKey && !vertexKey.StartsWith('!') ||
						GoogleAdc.HasConfiguredCredentials(model.Provider),
					AzureOpenAiResponsesProvider.ApiId => hasKey || hasCommandKey || HasVariable("AZURE_OPENAI_API_KEY"),
					_ => ((hasKey || hasCommandKey) && HasRequiredKeyEnvironment(model.Provider)) || hasOAuth,
				};
				providers[identity] = available;
			}
			return available;
		})];
	}

	private static CodexCredentials? ReadCodexCredentials()
	{
		try { return CodexAuth.Load(); }
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
		{
			// 一个损坏的 OAuth 条目不能阻止其它供应商显示或让桌面启动失败。
			return null;
		}
	}

	private static bool HasVariable(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

	/// Cloudflare 这类供应商的 API Key 只有在账户/网关 env 也齐备时才可用。
	/// 认证页会显示「缺少字段」，模型选择器也不该把它们当成可用模型。
	private static bool HasRequiredKeyEnvironment(string provider)
	{
		var option = ProviderAuthCatalog.Find(provider)?.ApiKey?.DefaultOption;
		if (option is null || option.EnvironmentFields.Count == 0) return true;
		var stored = ProviderEnvironment.StoredEnvironment(provider);
		return option.EnvironmentFields.All(field =>
			!field.Required || ProviderEnvironment.EnvironmentValue(stored, field.Name) is not null);
	}
}
