using System.Text;
using PiWeft.Ai.Auth;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ProviderAuthTests
{
	[Fact]
	public void AuthFlowParserFindsDeviceCodeAndBrowserUrl()
	{
		var device = AuthFlowParser.Parse("在 https://github.com/login/device 输入代码 ABCD-1234\n等待授权…");
		Assert.Equal("https://github.com/login/device", device.Url);
		Assert.Equal("ABCD-1234", device.Code);

		var browser = AuthFlowParser.Parse("在浏览器完成 Claude 登录：\nhttps://claude.ai/oauth/authorize?code=true&client_id=x\n");
		Assert.Equal("https://claude.ai/oauth/authorize?code=true&client_id=x", browser.Url);
		Assert.Null(browser.Code);

		Assert.Equal((null, null), AuthFlowParser.Parse("没有链接也没有验证码"));
	}

	[Fact]
	public void AuthStatusesReflectCredentialsExpiryAndLogout()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		try
		{
			var future = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
			var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
			OAuthStore.Save("anthropic",
				new OAuthCredential { Access = "access", Refresh = "refresh", Expires = future, AccountId = "demo" },
				directory.FullName);
			OAuthStore.Save("xai",
				new OAuthCredential { Access = "access", Refresh = "refresh", Expires = past },
				directory.FullName);
			CodexAuth.Save(new CodexCredentials { Access = "a.b.c", Refresh = "refresh", Expires = future },
				directory.FullName);
			AuthStore.SaveApiKey("openai", "sk-demo", directory.FullName);

			var statuses = ProviderAuthService.Statuses(directory.FullName).ToDictionary(status => status.Provider.Id);
			Assert.True(statuses["anthropic"].LoggedIn);
			Assert.False(statuses["anthropic"].Expired);
			Assert.Equal("demo", statuses["anthropic"].AccountId);
			Assert.True(statuses["xai"].LoggedIn);
			Assert.True(statuses["xai"].Expired);
			Assert.True(statuses["openai-codex"].LoggedIn);
			Assert.False(statuses["openai"].LoggedIn);
			Assert.True(statuses["openai"].ApiKeyConfigured);
			Assert.Equal(Strings.AuthApiKeyConfigured, statuses["openai"].StateText);

			Assert.True(ProviderAuthService.Logout("anthropic", directory.FullName));
			Assert.Null(OAuthStore.Read("anthropic", directory.FullName));
			Assert.True(OAuthStore.Read("xai", directory.FullName) is not null);
			Assert.True(CodexAuth.Load(directory.FullName) is not null);
			Assert.False(ProviderAuthService.Statuses(directory.FullName).Single(status => status.Provider.Id == "anthropic").LoggedIn);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public async Task AuthBridgesForwardTextAndPrompts()
	{
		var text = new StringBuilder();
		using (var writer = new AuthLogWriter(chunk => text.Append(chunk)))
		{
			writer.WriteLine("第一行");
			writer.Write('x');
		}
		Assert.Equal("第一行\nx", text.ToString());

		using var reader = new AuthPromptReader(_ => Task.FromResult<string?>("answer"));
		Assert.Equal("answer", await reader.ReadLineAsync());
	}

	[Fact]
	public void ProviderCatalogHasUniqueIdsAndDocs()
	{
		Assert.Equal(ProviderAuth.Providers.Count, ProviderAuth.Providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count());
		Assert.All(ProviderAuth.Providers, provider =>
		{
			Assert.False(string.IsNullOrWhiteSpace(provider.Name));
			Assert.StartsWith("https://", provider.DocsUrl, StringComparison.Ordinal);
		});
		Assert.NotNull(ProviderAuth.Find("openai-codex"));
		Assert.Null(ProviderAuth.Find("unknown"));
	}
}
