using PiWeft.CodingAgent.Models;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// <c>--offline</c> / <c>PI_OFFLINE</c> 与 <c>settings.httpProxy</c>。
/// 两者都要在 HTTP 客户端创建前生效：代理来自 <c>HttpClient.DefaultProxy</c> 首次读取时的环境变量。
/// </summary>
[Collection("process-environment")]
public class PiEnvironmentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-env-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _offlineEnv = Environment.GetEnvironmentVariable(PiEnvironment.OfflineVariable);
	private readonly string? _httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
	private readonly string? _httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
	private readonly bool _offline = PiEnvironment.Offline;

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, _offlineEnv);
		Environment.SetEnvironmentVariable("HTTP_PROXY", _httpProxy);
		Environment.SetEnvironmentVariable("HTTPS_PROXY", _httpsProxy);
		PiEnvironment.Offline = _offline;

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string AgentDir => Path.Combine(_root, "agent");
	private string Project => Path.Combine(_root, "project");

	private void WriteGlobal(string json)
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "settings.json"), json);
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("true", true)]
	[InlineData("YES", true)]
	[InlineData("0", false)]
	[InlineData("", false)]
	public void OfflineFollowsTheEnvironmentFlag(string value, bool expected)
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, value);
		PiEnvironment.Offline = false;

		PiEnvironment.Configure();

		Assert.Equal(expected, PiEnvironment.Offline);
	}

	[Fact]
	public void OfflineArgumentEnablesOfflineAndExportsTheEnvironmentVariable()
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, null);
		PiEnvironment.Offline = false;

		PiEnvironment.Configure(offlineArgument: true);

		Assert.True(PiEnvironment.Offline);
		Assert.Equal("1", Environment.GetEnvironmentVariable(PiEnvironment.OfflineVariable));
	}

	[Fact]
	public void ConfigureStaysOfflineWhenTheEnvironmentFlagIsSet()
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, "1");

		PiEnvironment.Configure();

		Assert.True(PiEnvironment.Offline);
	}

	[Fact]
	public void ConfigureCanReturnToOnlineWhenTheFlagIsCleared()
	{
		PiEnvironment.Configure(offlineArgument: true);
		Assert.True(PiEnvironment.Offline);

		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, null);
		PiEnvironment.Configure();

		Assert.False(PiEnvironment.Offline);
	}

	[Fact]
	public void ApplyHttpProxySetsBothEnvironmentVariables()
	{
		Environment.SetEnvironmentVariable("HTTP_PROXY", null);
		Environment.SetEnvironmentVariable("HTTPS_PROXY", null);

		PiEnvironment.ApplyHttpProxy("  http://proxy.local:8080 ");

		Assert.Equal("http://proxy.local:8080", Environment.GetEnvironmentVariable("HTTP_PROXY"));
		Assert.Equal("http://proxy.local:8080", Environment.GetEnvironmentVariable("HTTPS_PROXY"));
	}

	[Fact]
	public void ApplyHttpProxyLeavesEnvironmentAloneWhenUnset()
	{
		Environment.SetEnvironmentVariable("HTTP_PROXY", "http://existing:1");
		Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://existing:1");

		PiEnvironment.ApplyHttpProxy("   ");

		Assert.Equal("http://existing:1", Environment.GetEnvironmentVariable("HTTP_PROXY"));
		Assert.Equal("http://existing:1", Environment.GetEnvironmentVariable("HTTPS_PROXY"));
	}

	[Fact]
	public void HttpProxyIsGlobalOnly()
	{
		WriteGlobal("""{"httpProxy":"http://global:8080"}""");
		var projectDir = Path.Combine(Project, ".pi");
		Directory.CreateDirectory(projectDir);
		File.WriteAllText(Path.Combine(projectDir, "settings.json"), """{"httpProxy":"http://project:9090"}""");

		Assert.Equal("http://global:8080", CodingAgentSettings.Load(Project, AgentDir).HttpProxy);
	}

	[Fact]
	public async Task ModelCatalogRefreshIsSkippedOffline()
	{
		PiEnvironment.Offline = true;
		var calls = 0;

		var outcome = await ModelCatalogStore.RefreshAllAsync(
			agentDir: AgentDir,
			force: true,
			handler: new CountingHandler(() => calls++));

		Assert.True(outcome.NothingToDo);
		Assert.Equal(0, calls);
	}

	private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			onRequest();
			return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
			{
				Content = new StringContent("{}"),
			});
		}
	}
}
