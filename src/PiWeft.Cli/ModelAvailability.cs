using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using PiWeft.Ai.Providers.Bedrock;
using PiWeft.Ai.Providers.Google;
using PiWeft.Ai.Providers.OpenAi;

namespace PiWeft.Cli;

/// <summary>按已实现的认证路径筛选模型；只检查本地配置，不发请求或刷新令牌。</summary>
internal static class ModelAvailability
{
	public static IReadOnlyList<Model> Filter(IEnumerable<Model> models,
		IReadOnlyDictionary<string, string> configuredKeys, string? explicitKey = null)
	{
		var stored = AuthStore.ReadApiKeys();
		var credentials = ReadCodexCredentials();
		var providers = new Dictionary<(string Provider, string Api), bool>();
		return [.. models.Where(model =>
		{
			if (!CliEnvironment.SupportedApis.Contains(model.Api, StringComparer.Ordinal)) return false;
			var identity = (model.Provider, model.Api);
			if (!providers.TryGetValue(identity, out var available))
			{
				var configuredKey = configuredKeys.GetValueOrDefault(model.Provider);
				var key = explicitKey is { Length: > 0 } ? explicitKey : configuredKey is { Length: > 0 } ? configuredKey :
					stored.GetValueOrDefault(model.Provider) ?? Environment.GetEnvironmentVariable(ProviderEnvironment.KeyName(model.Provider));
				var hasKey = !string.IsNullOrWhiteSpace(key) && !key.StartsWith('!');
				available = model.Api switch
				{
					OpenAiCodexResponsesProvider.ApiId => !string.IsNullOrWhiteSpace(explicitKey) ||
						configuredKeys.GetValueOrDefault(model.Provider) is { Length: > 0 } configured && !configured.StartsWith('!') ||
						credentials is { Access.Length: > 0, Refresh.Length: > 0 },
					BedrockConverseProvider.ApiId => AwsCredentials.FromEnvironment().IsValid,
					GoogleVertexProvider.ApiId => !string.IsNullOrWhiteSpace(explicitKey) ||
						configuredKeys.GetValueOrDefault(model.Provider) is { Length: > 0 } vertexKey && !vertexKey.StartsWith('!') ||
						HasVariable("GOOGLE_CLOUD_API_KEY") || HasVariable(GoogleAdc.AccessTokenVariable) ||
						File.Exists(Environment.GetEnvironmentVariable(GoogleAdc.CredentialsVariable)),
					AzureOpenAiResponsesProvider.ApiId => hasKey || HasVariable("AZURE_OPENAI_API_KEY"),
					_ => hasKey,
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
}
