using System.Collections.Frozen;

namespace PiWeft.Ai.Auth;

/// 一个 API Key 登录字段。<see cref="Name"/> 为 <c>key</c> 时写 auth.json 的
/// <c>key</c> 字段，其它值写进同一个凭据的 <c>env</c> 对象（与 pi 的
/// <c>ApiKeyCredential.env</c> 语义一致）。
public sealed record ProviderKeyField(
	string Name,
	string Label,
	bool Secret = false,
	bool Required = true,
	string? Placeholder = null)
{
	public const string MainKey = "key";

	public bool IsMainKey => string.Equals(Name, MainKey, StringComparison.Ordinal);

	public bool IsEnvironment => !IsMainKey;
}

/// 一个可选的 API Key 登录方式（例如 Vertex 的 API Key / ADC / 服务账号）。
/// 没有字段的 <see cref="Ambient"/> 方式只写一条 <c>{ "type": "api_key" }</c> 标记，
/// 表示凭据来自进程环境或实例元数据。
public sealed record ProviderKeyOption(
	string Id,
	string Label,
	IReadOnlyList<ProviderKeyField> Fields,
	string? Description = null,
	bool Ambient = false)
{
	public bool HasMainKey => Fields.Any(static item => item.IsMainKey);

	public IReadOnlyList<ProviderKeyField> EnvironmentFields =>
		field ??= [.. Fields.Where(static item => item.IsEnvironment)];
}

/// 某供应商的 API Key 认证描述。API Key 与 OAuth 是 auth.json 里互斥的两种凭据类型：
/// 同一个供应商只保存一份，因此界面在覆盖另一种类型前必须明确提示。
public sealed record ProviderKeyAuth(
	string Label,
	IReadOnlyList<ProviderKeyOption> Options,
	IReadOnlyList<string> EnvironmentNames,
	string? Hint = null)
{
	public ProviderKeyOption DefaultOption => Options[0];

	public bool SupportsInteractive => Options.Any(static option =>
		option.Ambient || option.Fields.Count > 0);

	public bool SupportsEnvironment => EnvironmentNames.Count > 0;

	public ProviderKeyOption? Option(string? id) =>
		id is null ? null : Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));
}

public sealed record ProviderAuthDescriptor(
	string Id,
	string Name,
	ProviderKeyAuth? ApiKey,
	string DocsUrl,
	bool OAuth = false);

/// pi 内置供应商的认证目录。
///
/// 为什么在 Ai 层维护：CLI 与桌面端都要知道「这个供应商支持哪种登录、Key 写到哪里、
/// 额外字段叫什么」。把这些事实放进 UI 会让 `pi login` 与桌面端各写一份，最终对同一个
/// 供应商给出不同结论。OAuth 的具体流程仍在各自的 OAuth 实现里；这里只描述 API Key。
///
/// 字段与环境变量名对齐 pi `packages/ai/src/providers/*`：
/// 标准供应商只有一个 <c>key</c>；Cloudflare / Vertex / Bedrock 有额外 env 字段。
/// 不在这里的供应商（models.json 自定义）由状态页按「标准 API Key」处理。
public static class ProviderAuthCatalog
{
	private const string DocsBase = "https://pi.dev/docs/latest/providers#";

	public static IReadOnlyList<ProviderAuthDescriptor> BuiltIns { get; } = Build();

	private static readonly FrozenDictionary<string, ProviderAuthDescriptor> ById =
		BuiltIns.ToFrozenDictionary(static provider => provider.Id, StringComparer.Ordinal);

	public static FrozenSet<string> BuiltInIds { get; } =
		BuiltIns.Select(static provider => provider.Id).ToFrozenSet(StringComparer.Ordinal);

	public static ProviderAuthDescriptor? Find(string id) =>
		ById.GetValueOrDefault(id);

	/// 自定义供应商没有专用登录流程；仍允许把一个标准 API Key 写进 auth.json。
	public static ProviderKeyAuth StandardKey(string label, params string[] environmentNames) =>
		new(label,
			[new("api-key", "API Key", [new(ProviderKeyField.MainKey, "API Key", Secret: true)])],
			environmentNames);

	private static IReadOnlyList<ProviderAuthDescriptor> Build()
	{
		var providers = new List<ProviderAuthDescriptor>
		{
			Standard("amazon-bedrock", "Amazon Bedrock",
				new ProviderKeyAuth("AWS 凭据",
					[
						new("aws-keys", "AWS 访问密钥",
							[
								new("AWS_ACCESS_KEY_ID", "Access Key ID"),
								new("AWS_SECRET_ACCESS_KEY", "Secret Access Key", Secret: true),
								new("AWS_SESSION_TOKEN", "Session Token（可选）", Secret: true, Required: false),
							],
							"保存到 auth.json 的 env，请求时用于 SigV4 签名。"),
						new("aws-env", "现有环境变量", [],
							"使用进程环境中的 AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY；不在 auth.json 保存密钥。",
							Ambient: true),
					],
					["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"],
					"本端只实现 AWS 访问密钥签名，不支持 AWS 配置文件或实例元数据发现。")),
			Standard("ant-ling", "Ant Ling", "Ant Ling API key", "ANT_LING_API_KEY"),
			StandardWithOAuth("anthropic", "Anthropic", "Anthropic API key",
				["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN"]),
			Standard("azure", "Azure", StandardKey("Azure OpenAI API key", "AZURE_OPENAI_API_KEY", "AZURE_API_KEY")),
			Standard("baseten", "Baseten", "Baseten API key", "BASETEN_API_KEY"),
			Standard("cerebras", "Cerebras", "Cerebras API key", "CEREBRAS_API_KEY"),
			Standard("cloudflare-ai-gateway", "Cloudflare AI Gateway",
				new ProviderKeyAuth("Cloudflare API key",
					[
						new("api-key", "API Key",
							[
								new(ProviderKeyField.MainKey, "API Key", Secret: true),
								new("CLOUDFLARE_ACCOUNT_ID", "账户 ID"),
								new("CLOUDFLARE_GATEWAY_ID", "网关 ID"),
							],
							"账户 ID 与网关 ID 写入凭据 env，用于展开模型目录里的 URL 占位符。"),
					],
					["CLOUDFLARE_API_KEY"])),
			Standard("cloudflare-workers-ai", "Cloudflare Workers AI",
				new ProviderKeyAuth("Cloudflare API key",
					[
						new("api-key", "API Key",
							[
								new(ProviderKeyField.MainKey, "API Key", Secret: true),
								new("CLOUDFLARE_ACCOUNT_ID", "账户 ID"),
							],
							"账户 ID 写入凭据 env，用于展开模型目录里的 URL 占位符。"),
					],
					["CLOUDFLARE_API_KEY"])),
			Standard("deepseek", "DeepSeek", "DeepSeek API key", "DEEPSEEK_API_KEY"),
			Standard("fireworks", "Fireworks", "Fireworks API key", "FIREWORKS_API_KEY"),
			StandardWithOAuth("github-copilot", "GitHub Copilot", "GitHub Copilot token", ["COPILOT_GITHUB_TOKEN"]),
			Standard("google", "Google", "Gemini API key", "GEMINI_API_KEY"),
			Standard("google-vertex", "Google Vertex AI",
				new ProviderKeyAuth("Google Cloud credentials",
					[
						new("api-key", "Google Cloud API Key",
							[new(ProviderKeyField.MainKey, "Google Cloud API Key", Secret: true)]),
						new("adc", "Application Default Credentials",
							[
								new("GOOGLE_CLOUD_PROJECT", "项目 ID"),
								new("GOOGLE_CLOUD_LOCATION", "区域", Placeholder: "us-central1"),
								new("GOOGLE_APPLICATION_CREDENTIALS", "凭据文件路径（可选）", Required: false,
									Placeholder: "~/.config/gcloud/application_default_credentials.json"),
							],
							"使用 gcloud ADC；凭据文件留空时读取默认 ADC 路径。"),
						new("service-account", "服务账号文件",
							[
								new("GOOGLE_APPLICATION_CREDENTIALS", "凭据文件路径"),
								new("GOOGLE_CLOUD_PROJECT", "项目 ID"),
								new("GOOGLE_CLOUD_LOCATION", "区域", Placeholder: "us-central1"),
							],
							"项目、区域与凭据文件路径写入凭据 env。"),
					],
					["GOOGLE_CLOUD_API_KEY", "GOOGLE_ACCESS_TOKEN"],
					"ADC 方式需要本机已执行 gcloud auth application-default login。")),
			Standard("groq", "Groq", "Groq API key", "GROQ_API_KEY"),
			Standard("huggingface", "Hugging Face", "Hugging Face token", "HF_TOKEN"),
			StandardWithOAuth("kimi-coding", "Kimi For Coding", "Kimi API key", ["KIMI_API_KEY"]),
			StandardWithOAuth("meta", "Meta", "Meta Model API key", ["META_API_KEY"]),
			Standard("minimax", "MiniMax", "MiniMax API key", "MINIMAX_API_KEY"),
			Standard("minimax-cn", "MiniMax CN", "MiniMax CN API key", "MINIMAX_CN_API_KEY"),
			Standard("mistral", "Mistral", "Mistral API key", "MISTRAL_API_KEY"),
			Standard("moonshotai", "Moonshot AI", "Moonshot AI API key", "MOONSHOT_API_KEY"),
			Standard("moonshotai-cn", "Moonshot AI CN", "Moonshot AI API key", "MOONSHOT_API_KEY"),
			Standard("nvidia", "NVIDIA", "NVIDIA API key", "NVIDIA_API_KEY"),
			StandardWithOAuth("openai", "OpenAI", "OpenAI API key", ["OPENAI_API_KEY"]),
			// Codex 只认 CodexAuth 的 OAuth 设备码凭据；同键 API Key 不参与可用性筛选。
			new("openai-codex", "OpenAI Codex", null, Docs("openai-codex"), OAuth: true),
			Standard("opencode", "OpenCode Zen", "OpenCode API key", "OPENCODE_API_KEY"),
			Standard("opencode-go", "OpenCode Go", "OpenCode API key", "OPENCODE_API_KEY"),
			StandardWithOAuth("openrouter", "OpenRouter", "OpenRouter API key", ["OPENROUTER_API_KEY"]),
			Standard("qwen-token-plan", "Qwen Token Plan Team", "Qwen Token Plan Team API key",
				"QWEN_TOKEN_PLAN_TEAM_API_KEY", "QWEN_TOKEN_PLAN_API_KEY"),
			Standard("qwen-token-plan-cn", "Qwen Token Plan Team CN", "Qwen Token Plan Team CN API key",
				"QWEN_TOKEN_PLAN_TEAM_CN_API_KEY", "QWEN_TOKEN_PLAN_CN_API_KEY"),
			Standard("qwen-token-plan-individual", "Qwen Token Plan Individual", "Qwen Token Plan Individual API key",
				"QWEN_TOKEN_PLAN_INDIVIDUAL_API_KEY", "QWEN_TOKEN_PLAN_API_KEY"),
			Standard("qwen-token-plan-individual-cn", "Qwen Token Plan Individual CN", "Qwen Token Plan Individual CN API key",
				"QWEN_TOKEN_PLAN_INDIVIDUAL_CN_API_KEY", "QWEN_TOKEN_PLAN_CN_API_KEY"),
			StandardWithOAuth("radius", "Radius", "Radius API key", ["RADIUS_API_KEY"]),
			Standard("together", "Together", "Together API key", "TOGETHER_API_KEY"),
			Standard("typesafe", "TypeSafe", "TypeSafe API key", "TYPESAFE_API_KEY"),
			Standard("vercel-ai-gateway", "Vercel AI Gateway", "Vercel AI Gateway API key", "AI_GATEWAY_API_KEY"),
			StandardWithOAuth("xai", "xAI", "xAI API key", ["XAI_API_KEY"]),
			Standard("xiaomi", "Xiaomi", "Xiaomi API key", "XIAOMI_API_KEY"),
			Standard("xiaomi-token-plan-ams", "Xiaomi Token Plan AMS", "Xiaomi Token Plan AMS API key", "XIAOMI_TOKEN_PLAN_AMS_API_KEY"),
			Standard("xiaomi-token-plan-cn", "Xiaomi Token Plan CN", "Xiaomi Token Plan CN API key", "XIAOMI_TOKEN_PLAN_CN_API_KEY"),
			Standard("xiaomi-token-plan-sgp", "Xiaomi Token Plan SGP", "Xiaomi Token Plan SGP API key", "XIAOMI_TOKEN_PLAN_SGP_API_KEY"),
			Standard("zai", "Z.AI", "Z.AI API key", "ZAI_API_KEY"),
			Standard("zai-coding-cn", "Z.AI Coding CN", StandardKey("Z.AI Coding CN API key", "ZAI_CODING_CN_API_KEY", "ZAI_API_KEY")),
		};

		return [.. providers.OrderBy(static provider => provider.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private static ProviderAuthDescriptor Standard(string id, string name, string label, params string[] environmentNames) =>
		new(id, name, StandardKey(label, environmentNames), Docs(id));

	private static ProviderAuthDescriptor Standard(string id, string name, ProviderKeyAuth key) =>
		new(id, name, key, Docs(id));

	private static ProviderAuthDescriptor StandardWithOAuth(string id, string name, string label, string[] environmentNames) =>
		new(id, name, StandardKey(label, environmentNames), Docs(id), OAuth: true);

	private static string Docs(string id) => DocsBase + id;
}
