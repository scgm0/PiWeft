using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>D-06：供应商连接测试复用完整请求路径并区分成功/提供方错误/异常/取消。</summary>
public class ProviderProbeTests
{
	[Fact]
	public async Task Success_ReportsLatencyAndUsage()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueText("pong");

		var result = await ProviderProbe.TestAsync(script.Invoke, ScriptedSessions.Model(), new StreamOptions());

		Assert.True(result.Ok);
		Assert.Null(result.Error);
		Assert.Equal("pong", result.Text);
		Assert.NotNull(result.Usage);
		Assert.True(result.LatencyMs >= 0);

		// 探测请求被限制到 1 个输出 token 且关闭思考，避免花钱。
		Assert.True(script.LastOptions is { MaxTokens: 1, ThinkingLevel: ThinkingLevels.Off });
	}

	[Fact]
	public async Task ProviderError_IsReportedAsFailure()
	{
		var script = new ScriptedStreamFn();
		script.EnqueueError("bad api key");

		var result = await ProviderProbe.TestAsync(script.Invoke, ScriptedSessions.Model(), new StreamOptions());

		Assert.False(result.Ok);
		Assert.Contains("bad api key", result.Error);
	}

	[Fact]
	public async Task ThrownException_IsReportedAsFailure()
	{
		var result = await ProviderProbe.TestAsync(
			static (_, _, _) => throw new InvalidOperationException("boom"),
			ScriptedSessions.Model(),
			new StreamOptions());

		Assert.False(result.Ok);
		Assert.Contains("boom", result.Error);
	}

	[Fact]
	public async Task Cancellation_IsReportedWithoutThrowing()
	{
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		var result = await ProviderProbe.TestAsync(
			static (model, _, _) => new AssistantMessageEventStream(),
			ScriptedSessions.Model(),
			new StreamOptions(),
			cancellation.Token);

		Assert.False(result.Ok);
		Assert.Equal("已取消", result.Error);
	}

	[Fact]
	public async Task SessionTestConnection_UsesTheSessionStreamAndReportsLatency()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-probe-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			var script = new ScriptedStreamFn();
			script.EnqueueText("pong");

			using var session = new CodingAgentSession(new CodingAgentSessionOptions
			{
				Cwd = root,
				Model = ScriptedSessions.Model(),
				StreamFn = script.Invoke,
				Session = SessionManager.InMemory(root),
				Persist = false,
				Skills = [],
				PromptTemplates = [],
			});

			var result = await session.TestConnectionAsync();

			Assert.True(result.Ok);
			Assert.NotNull(result.Usage);
			Assert.Equal(1, script.LastOptions!.MaxTokens);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
