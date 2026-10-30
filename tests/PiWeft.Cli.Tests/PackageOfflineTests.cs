using PiWeft.Cli;
using PiWeft.CodingAgent;
using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// 包命令在通用参数解析之前分发，因此 <c>--offline</c> 必须由它自己识别。
/// 离线时 <c>update</c>（含 <c>--models</c>）不发起网络请求。
/// </summary>
[Collection("process-environment")]
public sealed class PackageOfflineTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-pkg-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _offline = Environment.GetEnvironmentVariable(PiEnvironment.OfflineVariable);
	private readonly string? _agentDir = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");

	public PackageOfflineTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, _offline);
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _agentDir);
		PiEnvironment.Configure();

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task UpdateModelsIsSkippedWhenOffline()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _root);
		var output = new StringWriter();
		var error = new StringWriter();

		var exit = await PackageCommands.RunAsync(
			"update",
			["--offline", "--models"],
			output,
			error,
			_root,
			CancellationToken.None);

		Assert.Equal(0, exit);
		Assert.Contains("离线模式", output.ToString());
		Assert.Empty(error.ToString());
	}

	[Fact]
	public async Task OfflineFromTheEnvironmentAlsoSkipsUpdates()
	{
		Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", _root);
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, "1");
		PiEnvironment.Configure();
		var output = new StringWriter();

		var exit = await PackageCommands.RunAsync(
			"update",
			["--models"],
			output,
			new StringWriter(),
			_root,
			CancellationToken.None);

		Assert.Equal(0, exit);
		Assert.Contains("离线模式", output.ToString());
	}
}
