using System.Diagnostics;
using PiWeft.Ai;
using PiWeft.Mcp;
using Xunit;

namespace PiWeft.Mcp.Tests;

/// 后台连接：立刻返回、工具随后到位、首轮只为 direct 工具等待。
public sealed class McpBackgroundConnectTests
{
	private static McpServerConfig Stdio(string name, string exposure) => new()
	{
		Name = name,
		Command = "definitely-not-installed-piweft-mcp-binary",
		Exposure = exposure,
	};

	[Fact]
	public async Task ReturnsImmediatelyAndFillsStatesInTheBackground()
	{
		var stopwatch = Stopwatch.StartNew();
		await using var connection = await McpServers.ConnectAllAsync(
			[Stdio("alpha", McpExposure.Codemode), Stdio("beta", McpExposure.Codemode)],
			onError: null,
			cancellationToken: CancellationToken.None,
			agentDir: Path.Combine(Path.GetTempPath(), "piweft-bg-" + Guid.NewGuid().ToString("N")[..8]),
			background: true);

		stopwatch.Stop();
		// 起不来的进程也可能瞬间失败，所以只要求调用本身不阻塞；状态随后必须到位。
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"background connect blocked for {stopwatch.Elapsed}");

		await connection.WaitForAllAsync(TimeSpan.FromSeconds(30));

		Assert.False(connection.IsConnecting);
		// 两个服务器都起不来，但状态与错误都已就位，且顺序与配置一致。
		Assert.Equal(["alpha", "beta"], connection.Servers.Select(static state => state.Name));
		Assert.All(connection.Servers, static state => Assert.False(state.Connected));
		Assert.Equal(2, connection.Errors.Count);
	}

	[Fact]
	public async Task WaitForDirectToolsReturnsImmediatelyWithoutDirectServers()
	{
		await using var connection = await McpServers.ConnectAllAsync(
			[Stdio("alpha", McpExposure.Codemode)],
			onError: null,
			cancellationToken: CancellationToken.None,
			agentDir: Path.Combine(Path.GetTempPath(), "piweft-bg-" + Guid.NewGuid().ToString("N")[..8]),
			background: true);

		var stopwatch = Stopwatch.StartNew();
		await connection.WaitForDirectToolsAsync(TimeSpan.FromSeconds(30));
		stopwatch.Stop();

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"waited {stopwatch.Elapsed} without direct servers");
	}

	[Fact]
	public void DirectExposureIsDetectedOnServerAndToolOverrides()
	{
		Assert.True(McpExposure.HasDirectTools(Stdio("a", McpExposure.Direct)));
		Assert.False(McpExposure.HasDirectTools(Stdio("a", McpExposure.Codemode)));
		Assert.False(McpExposure.HasDirectTools(Stdio("a", McpExposure.Deferred)));
		Assert.True(McpExposure.HasDirectTools(Stdio("a", McpExposure.Codemode) with
		{
			ToolExposure = new Dictionary<string, string>(StringComparer.Ordinal) { ["read_*"] = McpExposure.Direct },
		}));
	}

	[Fact]
	public async Task SynchronousConnectStillReturnsFilledConnection()
	{
		await using var connection = await McpServers.ConnectAllAsync(
			[Stdio("alpha", McpExposure.Codemode)],
			onError: null,
			cancellationToken: CancellationToken.None,
			agentDir: Path.Combine(Path.GetTempPath(), "piweft-bg-" + Guid.NewGuid().ToString("N")[..8]));

		Assert.False(connection.IsConnecting);
		Assert.Single(connection.Servers);
		Assert.Single(connection.Errors);
	}
}
