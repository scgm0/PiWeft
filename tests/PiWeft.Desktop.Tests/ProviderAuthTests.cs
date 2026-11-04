using System.Text;
using PiWeft.Ai;
using PiWeft.Ai.Auth;
using PiWeft.Ai.Providers;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("process-environment")]
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

			var statuses = ProviderAuthService.Statuses(directory.FullName).ToDictionary(status => status.Id);
			Assert.True(statuses["anthropic"].OAuthConfigured);
			Assert.False(statuses["anthropic"].OAuthExpired);
			Assert.True(statuses["anthropic"].CanLoginOAuth);
			Assert.True(statuses["anthropic"].CanSetKey);
			Assert.Equal("demo", statuses["anthropic"].AccountId);
			Assert.True(statuses["xai"].OAuthConfigured);
			Assert.True(statuses["xai"].OAuthExpired);
			Assert.True(statuses["openai-codex"].OAuthConfigured);
			Assert.Null(statuses["openai-codex"].Key);
			Assert.False(statuses["openai"].OAuthConfigured);
			Assert.True(statuses["openai"].KeyStored);
			Assert.True(statuses["openai"].KeyConfigured);
			Assert.True(statuses["openai"].LoggedIn);
			Assert.Equal(Strings.AuthKeyStored, statuses["openai"].KeyStateText);

			Assert.True(ProviderAuthService.Logout("anthropic", directory.FullName));
			Assert.Null(OAuthStore.Read("anthropic", directory.FullName));
			Assert.True(OAuthStore.Read("xai", directory.FullName) is not null);
			Assert.True(CodexAuth.Load(directory.FullName) is not null);
			Assert.False(ProviderAuthService.Statuses(directory.FullName).Single(status => status.Id == "anthropic").LoggedIn);

			// 删除 API Key 也走同一条「auth.json 条目」路径，不区分凭据类型。
			Assert.True(ProviderAuthService.Logout("openai", directory.FullName));
			Assert.Null(AuthStore.ReadStoredApiKey("openai", directory.FullName));
			Assert.False(ProviderAuthService.Statuses(directory.FullName).Single(status => status.Id == "openai").LoggedIn);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void AuthStatusesShowStoredEnvironmentAmbientAndCustomProviders()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		try
		{
			AuthStore.SaveApiKeyCredential("cloudflare-ai-gateway", "cf-key", new Dictionary<string, string>
			{
				["CLOUDFLARE_ACCOUNT_ID"] = "account",
				["CLOUDFLARE_GATEWAY_ID"] = "gateway",
			}, directory.FullName);
			var adc = Path.Combine(directory.FullName, "adc.json");
			File.WriteAllText(adc, "{}");
			AuthStore.SaveApiKeyCredential("google-vertex", null, new Dictionary<string, string>
			{
				["GOOGLE_CLOUD_PROJECT"] = "project",
				["GOOGLE_CLOUD_LOCATION"] = "us-central1",
				["GOOGLE_APPLICATION_CREDENTIALS"] = adc,
			}, directory.FullName);
			AuthStore.SaveApiKeyCredential("amazon-bedrock", null, null, directory.FullName);
			AuthStore.SaveApiKey("my-gateway", "custom-key", directory.FullName);

			var statuses = ProviderAuthService.Statuses(directory.FullName).ToDictionary(status => status.Id);
			var cloudflare = statuses["cloudflare-ai-gateway"];
			Assert.True(cloudflare.KeyStored);
			Assert.True(cloudflare.KeyConfigured);
			Assert.Contains("CLOUDFLARE_ACCOUNT_ID", cloudflare.KeyStateText);
			Assert.Contains("CLOUDFLARE_GATEWAY_ID", cloudflare.KeyStateText);
			Assert.Equal(cloudflare.KeyStateText, cloudflare.StateText);

			var previousAccount = Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID");
			try
			{
				Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", null);
				AuthStore.SaveApiKey("cloudflare-workers-ai", "legacy-key", directory.FullName);
				var incomplete = ProviderAuthService.Statuses(directory.FullName)
					.Single(item => item.Id == "cloudflare-workers-ai");
				Assert.True(incomplete.KeyIncomplete);
				Assert.Contains("账户 ID", incomplete.KeyStateText);
			}
			finally
			{
				Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", previousAccount);
			}

			var vertex = statuses["google-vertex"];
			Assert.True(vertex.KeyStored);
			Assert.False(vertex.StoredKey!.HasKey);
			Assert.True(vertex.KeyConfigured);
			Assert.Contains("GOOGLE_CLOUD_PROJECT", vertex.KeyStateText);

			var previousAccess = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
			var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
			try
			{
				Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", null);
				Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", null);
				var bedrock = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "amazon-bedrock");
				Assert.True(bedrock.KeyStored);
				Assert.False(bedrock.KeyConfigured);
				Assert.Equal(Strings.AuthKeyExternalMissing, bedrock.KeyStateText);

				Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "access");
				Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
				bedrock = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "amazon-bedrock");
				Assert.True(bedrock.KeyConfigured);
				Assert.Equal(Strings.AuthKeyExternal, bedrock.KeyStateText);
			}
			finally
			{
				Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousAccess);
				Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
			}

			Assert.False(statuses["openai-codex"].CanSetKey);

			var custom = statuses["my-gateway"];
			Assert.False(custom.BuiltIn);
			Assert.Equal("my-gateway", custom.Name);
			Assert.True(custom.KeyConfigured);
			Assert.True(custom.CanSetKey);
			Assert.Null(custom.OAuth);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void CustomProviderNamesComeFromModelsJson()
	{
		var root = Directory.CreateTempSubdirectory("piweft-auth-");
		try
		{
			var project = Path.Combine(root.FullName, "project");
			Directory.CreateDirectory(Path.Combine(project, ".pi"));
			File.WriteAllText(Path.Combine(project, ".pi", "models.json"),
				"""{"providers":{"my-gateway":{"name":"My Gateway","api":"openai-completions","baseUrl":"https://example.test/v1"}}}""");
			AuthStore.SaveApiKey("my-gateway", "custom-key", root.FullName);

			var status = ProviderAuthService.Statuses(root.FullName, project).Single(item => item.Id == "my-gateway");
			Assert.False(status.BuiltIn);
			Assert.Equal("My Gateway", status.Name);
			Assert.True(status.KeyConfigured);
		}
		finally { root.Delete(recursive: true); }
	}

	[Fact]
	public void VertexAdcWithMissingCredentialFileIsNotConfigured()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		try
		{
			AuthStore.SaveApiKeyCredential("google-vertex", null, new Dictionary<string, string>
			{
				["GOOGLE_CLOUD_PROJECT"] = "project",
				["GOOGLE_CLOUD_LOCATION"] = "us-central1",
				["GOOGLE_APPLICATION_CREDENTIALS"] = Path.Combine(directory.FullName, "missing.json"),
			}, directory.FullName);
			var status = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "google-vertex");
			Assert.True(status.AdcFileMissing);
			Assert.False(status.KeyConfigured);
			Assert.Equal(Strings.AuthKeyAdcFileMissing, status.KeyStateText);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void EnvironmentVariablesAreReportedWhenNothingIsStored()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		var previous = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
		try
		{
			Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "env-key");
			var status = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "deepseek");
			Assert.False(status.KeyStored);
			Assert.True(status.KeyConfigured);
			Assert.Equal("DEEPSEEK_API_KEY", status.EnvironmentSource);
			Assert.Equal(Strings.AuthKeyFromEnv("DEEPSEEK_API_KEY"), status.KeyStateText);
			Assert.True(status.LoggedIn);

			// 存储凭据优先于环境变量，状态行不能再声称来源是 env。
			AuthStore.SaveApiKey("deepseek", "stored-key", directory.FullName);
			status = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "deepseek");
			Assert.True(status.KeyStored);
			Assert.Null(status.EnvironmentSource);
			Assert.Equal(Strings.AuthKeyStored, status.KeyStateText);
		}
		finally
		{
			Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", previous);
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void EnvironmentApiKeyWithMissingProviderFieldsIsIncomplete()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		var previousKey = Environment.GetEnvironmentVariable("CLOUDFLARE_API_KEY");
		var previousAccount = Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID");
		try
		{
			Environment.SetEnvironmentVariable("CLOUDFLARE_API_KEY", "env-key");
			Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", null);
			var status = ProviderAuthService.Statuses(directory.FullName)
				.Single(item => item.Id == "cloudflare-workers-ai");
			Assert.False(status.KeyStored);
			Assert.True(status.KeyIncomplete);
			Assert.Contains("账户 ID", status.KeyStateText);
		}
		finally
		{
			Environment.SetEnvironmentVariable("CLOUDFLARE_API_KEY", previousKey);
			Environment.SetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID", previousAccount);
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void VertexAdcFromProcessEnvironmentShowsAsConfigured()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-");
		var adc = Path.Combine(directory.FullName, "adc.json");
		File.WriteAllText(adc, "{}");
		var previous = new Dictionary<string, string?>
		{
			["GOOGLE_APPLICATION_CREDENTIALS"] = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS"),
			["GOOGLE_CLOUD_PROJECT"] = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT"),
			["GOOGLE_CLOUD_LOCATION"] = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_LOCATION"),
			["GOOGLE_ACCESS_TOKEN"] = Environment.GetEnvironmentVariable("GOOGLE_ACCESS_TOKEN"),
			["GOOGLE_CLOUD_API_KEY"] = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_API_KEY"),
		};
		try
		{
			Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", adc);
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", "project");
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_LOCATION", "us-central1");
			Environment.SetEnvironmentVariable("GOOGLE_ACCESS_TOKEN", null);
			Environment.SetEnvironmentVariable("GOOGLE_CLOUD_API_KEY", null);

			var status = ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "google-vertex");
			Assert.False(status.KeyStored);
			Assert.True(status.KeyConfigured);
			Assert.Equal(Strings.AuthKeyAdc, status.KeyStateText);
		}
		finally
		{
			foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
			directory.Delete(recursive: true);
		}
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
	public void ProviderCatalogCoversEveryBuiltInProviderAndDocs()
	{
		Assert.Equal(ProviderAuth.Providers.Count, ProviderAuth.Providers.Select(provider => provider.Id).Distinct(StringComparer.Ordinal).Count());
		Assert.All(ProviderAuth.Providers, provider =>
		{
			Assert.False(string.IsNullOrWhiteSpace(provider.Name));
			Assert.StartsWith("https://", provider.DocsUrl, StringComparison.Ordinal);
		});
		Assert.NotNull(ProviderAuth.Find("openai-codex"));
		Assert.Null(ProviderAuth.Find("unknown"));

		var catalogIds = ProviderAuthCatalog.BuiltIns.Select(provider => provider.Id).ToHashSet(StringComparer.Ordinal);
		var modelIds = ModelRegistry.LoadDefault().Providers.ToHashSet(StringComparer.Ordinal);
		Assert.Empty(modelIds.Except(catalogIds));
		Assert.Equal(ProviderAuthCatalog.BuiltIns.Count, catalogIds.Count);
		Assert.All(ProviderAuthCatalog.BuiltIns, provider =>
		{
			Assert.False(string.IsNullOrWhiteSpace(provider.Name));
			Assert.StartsWith("https://pi.dev/docs/latest/providers#", provider.DocsUrl, StringComparison.Ordinal);
		});
		// 桌面 OAuth 列表与目录的 OAuth 标记不能漂移：两边任一新增都会在这里被拦下。
		Assert.All(ProviderAuth.Providers, provider => Assert.True(ProviderAuthCatalog.Find(provider.Id)!.OAuth));
		Assert.All(ProviderAuthCatalog.BuiltIns.Where(provider => provider.OAuth), provider => Assert.NotNull(ProviderAuth.Find(provider.Id)));
	}

	[Fact]
	public void ProviderSpecificKeyFieldsMatchPi()
	{
		var workers = ProviderAuthCatalog.Find("cloudflare-workers-ai")!.ApiKey!;
		Assert.Contains("CLOUDFLARE_ACCOUNT_ID", workers.DefaultOption.Fields.Select(field => field.Name));

		var gateway = ProviderAuthCatalog.Find("cloudflare-ai-gateway")!.ApiKey!;
		Assert.Contains("CLOUDFLARE_GATEWAY_ID", gateway.DefaultOption.Fields.Select(field => field.Name));

		var vertex = ProviderAuthCatalog.Find("google-vertex")!.ApiKey!;
		Assert.Equal(new[] { "api-key", "adc", "service-account" }, vertex.Options.Select(option => option.Id).ToArray());
		Assert.Contains("GOOGLE_CLOUD_PROJECT", vertex.Options[1].Fields.Select(field => field.Name));
		Assert.True(ProviderAuthCatalog.Find("openai-codex")!.ApiKey is null);
		Assert.True(ProviderAuthCatalog.Find("openai-codex")!.OAuth);

		var bedrock = ProviderAuthCatalog.Find("amazon-bedrock")!.ApiKey!;
		Assert.Contains("AWS_SECRET_ACCESS_KEY", bedrock.DefaultOption.Fields.Select(field => field.Name));
		Assert.True(bedrock.DefaultOption.Fields.Single(field => field.Name == "AWS_SECRET_ACCESS_KEY").Secret);
		Assert.True(bedrock.Options.Single(option => option.Id == "aws-env").Ambient);

		Assert.Contains("ANTHROPIC_API_KEY", ProviderAuthCatalog.Find("anthropic")!.ApiKey!.EnvironmentNames);
		Assert.True(ProviderAuthCatalog.Find("anthropic")!.OAuth);
	}
}
