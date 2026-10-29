using System.Text;
using PiWeft.Ai;
using PiWeft.Ai.Providers;

namespace PiWeft.Desktop;

/// <summary>
/// Git 提交摘要所需的会话能力。界面只依赖这组委托，模型网关与会话生命周期留在宿主。
/// </summary>
public sealed record GitCommitAssistant(
	Func<IReadOnlyList<Model>> Models,
	Func<Model> CurrentModel,
	Func<string> CurrentThinking,
	Func<Model, string, string, CancellationToken, Task<string>> Generate,
	Func<bool> IsBusy,
	Action? OpenSettings = null);

/// <summary>
/// 提交摘要失败的可判别原因。对齐 PiDeck 的 <c>GIT_COMMIT_*</c> 错误码：界面据此给出
/// 不同动作（去暂存 / 去设置 / 稍后重试 / 展开原始报错），而不是所有失败都一句「生成失败」。
/// </summary>
internal enum GitSummaryFailure
{
	/// <summary>暂存区没有改动。</summary>
	EmptyDiff,

	/// <summary>读暂存区 diff 失败（仓库损坏 / git 超时 / 目录失效）。</summary>
	DiffFailed,

	/// <summary>没有可用的对话模型（模型目录为空，或固定模型已不存在）。</summary>
	ModelMissing,

	/// <summary>会话正在运行，同一时间只跑一次摘要。</summary>
	Busy,

	/// <summary>超过 60 秒没有结果。</summary>
	Timeout,

	/// <summary>其它失败：模型返回错误、网络、取消以外的异常。</summary>
	Failed,
}

/// <summary>带失败原因的提交摘要异常；<see cref="Exception.Message"/> 已是可直接展示的中文。</summary>
internal sealed class GitSummaryException(GitSummaryFailure failure, string message, string? detail = null) : Exception(message)
{
	public GitSummaryFailure Failure { get; } = failure;

	/// <summary>原始报错（git 输出、模型错误文本），只进失败详情。</summary>
	public string? Detail { get; } = detail;
}

/// <summary>
/// 用暂存区 diff 生成一条提交信息。默认跟随当前会话模型/思考级别；偏好里指定固定模型时
/// 覆盖为固定项，虚拟模型按 <see cref="VirtualModels.RouteAsync"/> 先路由到物理模型。
/// </summary>
/// <remarks>
/// 提示词模板对齐 PiDeck：默认是「Gitmoji + 分点正文」的完整模板，用户可在
/// 「设置 → Git 提交摘要」里自定义，<c>{diff}</c> 是暂存区 diff 的占位符；
/// 偏好为空时使用 <see cref="DefaultPromptTemplate"/>。
/// </remarks>
internal static class GitCommitMessage
{
	/// <summary>塞进 prompt 的 diff 上限：够看清改了什么，又不会让一次摘要请求过大。</summary>
	public const int MaxDiffCharacters = 8000;

	/// <summary>
	/// 一次摘要的等待上限。PiDeck 用 60 秒，但它的摘要进程不带推理预算；这里默认跟随会话的
	/// 思考级别，一次 thinking 往返在慢网关上会超过 60 秒，所以放宽到 90 秒。
	/// 界面进度条与取消令牌都读这一个数，免得两处各写一份互相漂移。
	/// </summary>
	public const int GenerationTimeoutSeconds = 90;

	/// <summary>模板里代表暂存区 diff 的占位符（与 PiDeck 的 <c>{diff}</c> 同名）。</summary>
	public const string DiffPlaceholder = "{diff}";

	/// <summary>
	/// 默认提示词模板。第一行是 Gitmoji + Conventional 前缀的摘要，正文用 - 分点；
	/// 与 PiWeft 仓库的提交历史风格一致（<c>✨ feat(desktop): …</c>），旧模板只有一句摘要。
	/// </summary>
	public const string DefaultPromptTemplate =
		"请根据以下 git diff 生成一条中文 git commit message。\n" +
		"\n" +
		"变更描述：\n" +
		DiffPlaceholder + "\n" +
		"\n" +
		"Gitmoji 对应关系：\n" +
		"✨ feat - 新功能\n" +
		"🐛 fix - Bug 修复\n" +
		"📚 docs - 文档更新\n" +
		"💎 style - 代码格式\n" +
		"♻️ refactor - 重构\n" +
		"🧪 test - 测试\n" +
		"⚡ perf - 性能优化\n" +
		"🔧 chore - 构建/工具\n" +
		"\n" +
		"要求：\n" +
		"1. 使用对应的 Gitmoji 开头，格式为「emoji type(scope): 摘要」，scope 取改动最集中的模块（如 desktop、git、ai），没有明显模块可省略；\n" +
		"2. 第一行不超过 72 个字符，用祈使句描述做了什么，不加句号；\n" +
		"3. 空一行后用 - 分点列出具体变更点（最多 4 条），写清改了什么与为什么改；\n" +
		"4. 使用中文；代码标识符、路径、命令与专有名词保留原文；\n" +
		"5. 只输出提交信息本身，不要代码块、引号、前缀或解释。";

	/// <summary>模板是否等于默认值（偏好里存空串表示“跟随默认”）。</summary>
	public static bool IsDefaultTemplate(string? template) =>
		string.Equals((template ?? string.Empty).Trim(), DefaultPromptTemplate, StringComparison.Ordinal);

	/// <summary>把偏好里的模板归一化成可用模板：空白即默认。</summary>
	public static string NormalizeTemplate(string? template) =>
		string.IsNullOrWhiteSpace(template) ? DefaultPromptTemplate : template.Trim();

	/// <summary>
	/// 用模板拼 prompt。<c>{diff}</c> 会被替换成真实 diff；模板忘了写占位符时附在末尾，
	/// 否则摘要请求会退化成“让模型猜改了什么”。
	/// </summary>
	public static string BuildPrompt(string diff, string? template = null)
	{
		var value = NormalizeTemplate(template);
		if (!value.Contains(DiffPlaceholder, StringComparison.Ordinal))
		{
			value += "\n\n<diff>\n" + DiffPlaceholder + "\n</diff>";
		}

		return value.Replace(DiffPlaceholder, diff, StringComparison.Ordinal);
	}

	public static async Task<string> GenerateAsync(
		ModelsGateway gateway,
		ModelRegistry registry,
		Model model,
		string thinking,
		string diff,
		Func<string, string?> apiKeyForProvider,
		CancellationToken cancellationToken) =>
		await GenerateAsync(gateway, registry, model, thinking, diff, promptTemplate: null, apiKeyForProvider, cancellationToken)
			.ConfigureAwait(false);

	public static async Task<string> GenerateAsync(
		ModelsGateway gateway,
		ModelRegistry registry,
		Model model,
		string thinking,
		string diff,
		string? promptTemplate,
		Func<string, string?> apiKeyForProvider,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(diff))
		{
			throw new GitSummaryException(GitSummaryFailure.EmptyDiff, Strings.GitStageBeforeGenerate);
		}

		var prompt = BuildPrompt(diff, promptTemplate);
		var messages = new Message[]
		{
			new UserMessage
			{
				Content = MessageContent.FromText(prompt),
				Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			},
		};

		var resolved = model;
		var resolvedThinking = thinking;
		if (VirtualModels.IsVirtual(model))
		{
			var definition = registry.Virtual(model.Provider, model.Id)
				?? throw new GitSummaryException(
					GitSummaryFailure.ModelMissing,
					Strings.GitSummaryModelMissingDetail($"{model.Provider}/{model.Id}"));

			var route = await VirtualModels.RouteAsync(
				definition,
				new VirtualModelRequest
				{
					Model = model,
					ThinkingLevel = thinking,
					Reason = VirtualRouteReason.Direct,
					Messages = messages,
					Registry = registry,
				},
				cancellationToken).ConfigureAwait(false);
			resolved = route.Model;
			resolvedThinking = route.ThinkingLevel ?? thinking;
		}

		// 一次摘要请求：返回收好的正文与是否被输出上限截断。
		async Task<(string Text, bool Truncated)> RunOnce(string level)
		{
			var stream = gateway.Stream(
				resolved,
				new Context { Messages = messages },
				new StreamOptions
				{
					ApiKey = apiKeyForProvider(resolved.Provider),
					ThinkingLevel = ThinkingLevelResolver.Clamp(resolved, level),
					MaxTokens = OutputBudget(resolved, level),
					CacheRetention = CacheRetentions.None,
					CancellationToken = cancellationToken,
				});

			var builder = new StringBuilder();
			var truncated = false;
			await foreach (var @event in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				switch (@event)
				{
					case TextDeltaEvent delta:
						builder.Append(delta.Delta);
						break;
					case DoneEvent done:
						truncated = done.Reason == StopReason.Length;
						break;
					case ErrorEvent error:
						var message = error.Error.ErrorMessage is { Length: > 0 } text ? text : Strings.GitSummaryFailed;
						throw new GitSummaryException(GitSummaryFailure.Failed, message);
				}
			}

			return (Sanitize(builder.ToString()), truncated);
		}

		var first = await RunOnce(resolvedThinking).ConfigureAwait(false);
		if (first.Text.Length > 0)
		{
			return first.Text;
		}

		// 开了思考档却一个正文都没回：几乎总是输出预算被推理吃掉了。兼容端点（Kimi/Moonshot
		// 这类 openai-completions）不会像 Anthropic 那样把 max_tokens 抬到预算之上，旧代码写死的
		// 512 token 会全部花在 reasoning 上，看起来就是「模型没有返回提交摘要」。
		// 拿不带思考的档位再试一次：只多花一次请求，不让整个功能在高档位下摸不到东西。
		if (!string.Equals(ThinkingLevelResolver.Clamp(resolved, resolvedThinking), ThinkingLevels.Off, StringComparison.Ordinal))
		{
			var retry = await RunOnce(ThinkingLevels.Off).ConfigureAwait(false);
			if (retry.Text.Length > 0)
			{
				return retry.Text;
			}

			first = retry;
		}

		throw new GitSummaryException(
			GitSummaryFailure.Failed,
			first.Truncated ? Strings.GitSummaryTruncated : Strings.GitSummaryEmpty);
	}

	/// <summary>
	/// 一次摘要的输出预算。不思考时一条提交信息用不了多少；开思考时必须给推理留出空间，
	/// 否则兼容端点（openai-completions / openai-responses 这类）会把额度全花在 reasoning 上，
	/// 正文一个字都不返回，看起来就是「模型没有返回提交摘要」。旧代码写死的 512 正是这么失败的。
	/// 上限取模型的 maxTokens 与 <see cref="MaxThinkingBudget"/> 的较小值：给够推理空间，
	/// 又不让一次摘要拿满几十万 token 的预算把等待时间拖到用户以为卡死，也从不越过模型自己声明的上限。
	/// </summary>
	internal static int OutputBudget(Model model, string thinking) =>
		string.Equals(ThinkingLevelResolver.Clamp(model, thinking), ThinkingLevels.Off, StringComparison.Ordinal)
			? Math.Min(Math.Max(model.MaxTokens, 512), 1024)
			: Math.Min(model.MaxTokens, MaxThinkingBudget);

	/// <summary>开思考时一次摘要最多要多少输出 token：够写完一轮推理 + 一条提交信息。</summary>
	private const int MaxThinkingBudget = 16384;

	/// <summary>去掉模型常见的代码围栏/引号/前缀，并限制成一条可直接提交的消息。</summary>
	internal static string Sanitize(string text)
	{
		var value = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
		if (value.Length == 0)
		{
			return string.Empty;
		}

		// 整段包在 ```…``` 里：去掉围栏行（保留内容里的单个反引号）。
		if (value.StartsWith("```", StringComparison.Ordinal))
		{
			var newline = value.IndexOf('\n');
			value = newline >= 0 ? value[(newline + 1)..] : value[3..];
			value = value.TrimEnd();
			if (value.EndsWith("```", StringComparison.Ordinal))
			{
				value = value[..^3].TrimEnd();
			}
		}

		var lines = value.Split('\n').ToList();
		while (lines.Count > 0 && lines[0].Trim().Length == 0)
		{
			lines.RemoveAt(0);
		}

		if (lines.Count > 0)
		{
			var first = lines[0].Trim();
			foreach (var prefix in new[] { "commit message:", "提交信息：", "提交信息:", "提交摘要：", "提交摘要:" })
			{
				if (first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					lines[0] = first[prefix.Length..].Trim();
					break;
				}
			}
		}

		// 只保留最多 12 行；连续空行压成一行，避免模型在末尾堆一大段空白。
		var kept = new List<string>();
		foreach (var raw in lines)
		{
			var line = raw.TrimEnd();
			if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0))
			{
				continue;
			}

			kept.Add(line);
			if (kept.Count >= 12)
			{
				break;
			}
		}

		var result = string.Join('\n', kept).Trim();
		if (result.Length >= 2 &&
			((result[0] == '"' && result[^1] == '"') || (result[0] == '\'' && result[^1] == '\'')))
		{
			result = result[1..^1].Trim();
		}

		return result.Length <= 4096 ? result : result[..4096].TrimEnd();
	}
}
