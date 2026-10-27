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
/// 用暂存区 diff 生成一条提交信息。默认跟随当前会话模型/思考级别；偏好里指定固定模型时
/// 覆盖为固定项，虚拟模型按 <see cref="VirtualModels.RouteAsync"/> 先路由到物理模型。
/// </summary>
internal static class GitCommitMessage
{
	/// <summary>塞进 prompt 的 diff 上限：够看清改了什么，又不会让一次摘要请求过大。</summary>
	public const int MaxDiffCharacters = 8000;

	public static string BuildPrompt(string diff) =>
		"你是 Git 提交信息助手。根据下面的暂存区 diff 写一条提交信息。\n" +
		"要求：\n" +
		"1. 第一行是简洁的摘要，使用祈使句，不超过 72 个字符；\n" +
		"2. 如需正文，空一行后最多写 3 行，说明改动与原因；\n" +
		"3. 使用中文；代码标识符、路径和专有名词保留原文；\n" +
		"4. 只输出提交信息本身，不要代码块、引号、前缀或解释。\n\n" +
		"<diff>\n" + diff + "\n</diff>";

	public static async Task<string> GenerateAsync(
		ModelsGateway gateway,
		ModelRegistry registry,
		Model model,
		string thinking,
		string diff,
		Func<string, string?> apiKeyForProvider,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(diff))
		{
			throw new InvalidOperationException(Strings.GitStageBeforeGenerate);
		}

		var prompt = BuildPrompt(diff);
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
				?? throw new InvalidOperationException($"Virtual model {model.Provider}/{model.Id} is not registered.");
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

		var stream = gateway.Stream(
			resolved,
			new Context { Messages = messages },
			new StreamOptions
			{
				ApiKey = apiKeyForProvider(resolved.Provider),
				ThinkingLevel = ThinkingLevelResolver.Clamp(resolved, resolvedThinking),
				MaxTokens = 512,
				CacheRetention = CacheRetentions.None,
				CancellationToken = cancellationToken,
			});

		var builder = new StringBuilder();
		await foreach (var @event in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
		{
			switch (@event)
			{
				case TextDeltaEvent delta:
					builder.Append(delta.Delta);
					break;
				case ErrorEvent error:
					throw new InvalidOperationException(
						error.Error.ErrorMessage is { Length: > 0 } message ? message : Strings.GitSummaryFailed);
			}
		}

		var text = Sanitize(builder.ToString());
		if (text.Length == 0)
		{
			throw new InvalidOperationException(Strings.GitSummaryEmpty);
		}

		return text;
	}

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
