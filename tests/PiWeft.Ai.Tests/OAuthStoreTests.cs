using System.Text.Json.Nodes;
using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class OAuthStoreTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-oauth-" + Guid.NewGuid().ToString("N")[..8]);

	public OAuthStoreTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void SaveAndReadRoundTripsOAuthCredential()
	{
		OAuthStore.Save("anthropic", new OAuthCredential
		{
			Access = "sk-ant-oat-1",
			Refresh = "refresh-1",
			Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
			ClientId = "client",
			Scopes = ["a", "b"],
		}, _root);

		var read = OAuthStore.Read("anthropic", _root);

		Assert.NotNull(read);
		Assert.Equal("sk-ant-oat-1", read!.Access);
		Assert.Equal("refresh-1", read.Refresh);
		Assert.Equal(["a", "b"], read.Scopes);
	}

	[Fact]
	public void RemovingOneProviderKeepsApiKeysAndOtherEntries()
	{
		var path = Path.Combine(_root, "auth.json");
		File.WriteAllText(path, """{"deepseek":{"type":"api_key","key":"keep"},"xai":{"type":"oauth","access":"a","refresh":"r","expires":1}}""");

		Assert.True(OAuthStore.RemoveProvider("xai", _root));

		var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		Assert.Null(root["xai"]);
		Assert.Equal("keep", root["deepseek"]!["key"]!.GetValue<string>());
	}
}
