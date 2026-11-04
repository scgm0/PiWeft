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
	public async Task ReadAsyncRoundTripsOAuthCredential()
	{
		OAuthStore.Save("anthropic", new OAuthCredential
		{
			Access = "sk-ant-oat-async",
			Refresh = "refresh-async",
			Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, _root);

		var read = await OAuthStore.ReadAsync("anthropic", _root);

		Assert.NotNull(read);
		Assert.Equal("sk-ant-oat-async", read!.Access);
		Assert.Equal("refresh-async", read.Refresh);
	}

	[Fact]
	public void ReadAllReturnsEveryOAuthCredentialInOnePass()
	{
		OAuthStore.Save("anthropic", new OAuthCredential { Access = "a", Refresh = "r", Expires = 1 }, _root);
		OAuthStore.Save("xai", new OAuthCredential { Access = "x", Refresh = "r", Expires = 2 }, _root);
		File.WriteAllText(Path.Combine(_root, "auth.json"),
			"""{"anthropic":{"type":"oauth","access":"a","refresh":"r","expires":1},"xai":{"type":"oauth","access":"x","refresh":"r","expires":2},"deepseek":{"type":"api_key","key":"k"},"broken":{"type":"oauth","access":123}}""");

		var all = OAuthStore.ReadAll(_root);
		Assert.Equal(2, all.Count);
		Assert.Equal("a", all["anthropic"].Access);
		Assert.Equal("x", all["xai"].Access);
		Assert.False(all.ContainsKey("deepseek"));
		Assert.Null(OAuthStore.Read("broken", _root));
	}

	[Fact]
	public void ReadAccessTokenHonorsExpiryAndSkew()
	{
		OAuthStore.Save("future", new OAuthCredential
		{
			Access = "future-access",
			Refresh = "r",
			Expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, _root);
		OAuthStore.Save("expired", new OAuthCredential
		{
			Access = "expired-access",
			Refresh = "r",
			Expires = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
		}, _root);

		Assert.Equal("future-access", OAuthStore.ReadAccessToken("future", _root));
		Assert.Null(OAuthStore.ReadAccessToken("expired", _root));
		// 提前 2 小时的 skew 让 1 小时后的凭据也算过期。
		Assert.Null(OAuthStore.ReadAccessToken("future", _root, TimeSpan.FromHours(2)));
	}

	[Fact]
	public async Task ConcurrentAuthWritesDoNotLoseProviders()
	{
		await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
			OAuthStore.Save($"provider-{index}",
				new OAuthCredential { Access = $"a{index}", Refresh = "r", Expires = 2 }, _root))));
		await Task.WhenAll(
			Task.Run(() => AuthStore.SaveApiKey("deepseek", "sk-key", _root)),
			Task.Run(() => CodexAuth.Save(
				new CodexCredentials { Access = "codex-access", Refresh = "codex-refresh", Expires = 2 }, _root)));

		var root = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "auth.json")))!.AsObject();
		Assert.Equal(34, root.Count);
		Assert.Equal("sk-key", root["deepseek"]!["key"]!.GetValue<string>());
		Assert.Equal("codex-access", root["openai-codex"]!["access"]!.GetValue<string>());
	}

	[Fact]
	public void SaveRefusesToOverwriteMalformedAuthFile()
	{
		var path = Path.Combine(_root, "auth.json");
		File.WriteAllText(path, "{not json");
		Assert.Throws<CodexAuthException>(() => OAuthStore.Save("anthropic",
			new OAuthCredential { Access = "a", Refresh = "r", Expires = 1 }, _root));
		Assert.Equal("{not json", File.ReadAllText(path));
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
