using System.Diagnostics;
using PiWeft.Agent;
using PiWeft.Ai;

namespace PiWeft.CodingAgent;

/// <summary>一次供应商连通性测试的结果。</summary>
public sealed record ProviderTestResult(bool Ok, long LatencyMs, string? Text, Usage? Usage, string? Error);

/// <summary>
/// 供应商连接测试：用当前 provider 的完整请求路径发一次极小的请求
/// （<c>maxTokens = 1</c>、关闭思考），验证 Base URL、凭据、模型 ID 与自定义头是否可用，
/// 并记录端到端延迟与实际用量。所有 provider 共用同一实现，不需要各自的探测代码。
/// </summary>
public static class ProviderProbe
{
	/// <summary>探测提示词；越短越省 token。</summary>
	private const string ProbeText = "ping";

	/// <summary>默认探测超时，避免不可达端点挂住配置界面。</summary>
	public const int DefaultTimeoutMs = 15_000;

	public static async Task<ProviderTestResult> TestAsync(
		StreamFn stream,
		Model model,
		StreamOptions options,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(model);

		var stopwatch = Stopwatch.StartNew();
		try
		{
			var messages = new List<Message>
			{
				new UserMessage
				{
					Content = MessageContent.FromText(ProbeText),
					Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				},
			};

			var probe = options with
			{
				MaxTokens = 1,
				ThinkingLevel = ThinkingLevels.Off,
				TimeoutMs = options.TimeoutMs ?? DefaultTimeoutMs,
			};

			var assistant = await stream(model, messages, probe)
				.Result
				.WaitAsync(cancellationToken)
				.ConfigureAwait(false);

			stopwatch.Stop();
			var text = string.Concat(assistant.Content.OfType<TextContent>().Select(static block => block.Text));

			return assistant.StopReason is StopReason.Error or StopReason.Aborted
				? new ProviderTestResult(false, stopwatch.ElapsedMilliseconds, null, assistant.Usage, assistant.ErrorMessage ?? "unknown provider error")
				: new ProviderTestResult(true, stopwatch.ElapsedMilliseconds, text, assistant.Usage, null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			stopwatch.Stop();
			return new ProviderTestResult(false, stopwatch.ElapsedMilliseconds, null, null, "已取消");
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			return new ProviderTestResult(false, stopwatch.ElapsedMilliseconds, null, null, ex.Message);
		}
	}
}
