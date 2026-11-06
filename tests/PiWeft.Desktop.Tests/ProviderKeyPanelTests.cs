using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai.Auth;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ProviderKeyPanelTests
{
	[Fact]
	public void PanelWritesKeyAndProviderEnvironmentFields()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-key-panel-");
		try
		{
			var status = ProviderAuthService.Statuses(directory.FullName)
				.Single(item => item.Id == "cloudflare-ai-gateway");
			ProviderKeyPanel? saved = null;
			var forgotten = true;
			var panel = new ProviderKeyPanel(status, directory.FullName, (owner, cleared) =>
			{
				saved = owner;
				forgotten = cleared;
			});
			var window = new Window();
			panel.Show(window);
			panel.View.Measure(new Size(1000, 720));
			panel.View.Arrange(new Rect(0, 0, 1000, 720));
			Assert.True(panel.View.IsVisible);
			var secret = Find<PasswordBox>(panel.View).Single();
			Assert.True(secret.Bounds.Width > 100, $"密钥输入宽度 {secret.Bounds.Width}");

			panel.FillForSmoke("cf-key", new Dictionary<string, string>
			{
				["CLOUDFLARE_ACCOUNT_ID"] = "account",
				["CLOUDFLARE_GATEWAY_ID"] = "gateway",
			});
			panel.SaveForSmoke();

			Assert.Same(panel, saved);
			Assert.False(forgotten);
			Assert.False(panel.View.IsVisible);
			Assert.Equal(0, window.OverlayLayer.Count);

			var stored = AuthStore.ReadStoredApiKey("cloudflare-ai-gateway", directory.FullName)!;
			Assert.Equal("cf-key", stored.Key);
			Assert.Equal("account", stored.Environment["CLOUDFLARE_ACCOUNT_ID"]);
			Assert.Equal("gateway", stored.Environment["CLOUDFLARE_GATEWAY_ID"]);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void SwitchingToAdcSavesEnvironmentWithoutAKey()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-key-panel-");
		try
		{
			var status = ProviderAuthService.Statuses(directory.FullName)
				.Single(item => item.Id == "google-vertex");
			var panel = new ProviderKeyPanel(status, directory.FullName, (_, _) => { });
			panel.SelectMethodForSmoke("adc");
			panel.FillForSmoke(null, new Dictionary<string, string>
			{
				["GOOGLE_CLOUD_PROJECT"] = "project",
				["GOOGLE_CLOUD_LOCATION"] = "us-central1",
			});
			panel.SaveForSmoke();

			var stored = AuthStore.ReadStoredApiKey("google-vertex", directory.FullName)!;
			Assert.False(stored.HasKey);
			Assert.Equal("project", stored.Environment["GOOGLE_CLOUD_PROJECT"]);
			Assert.Equal("us-central1", stored.Environment["GOOGLE_CLOUD_LOCATION"]);

			// 再次打开要落在 ADC 方式上，不回空的 API Key 表单。
			var reopened = new ProviderKeyPanel(
				ProviderAuthService.Statuses(directory.FullName).Single(item => item.Id == "google-vertex"),
				directory.FullName, (_, _) => { });
			reopened.FillForSmoke(null, new Dictionary<string, string>
			{
				["GOOGLE_CLOUD_PROJECT"] = "project-2",
				["GOOGLE_CLOUD_LOCATION"] = "europe-west1",
			});
			reopened.SaveForSmoke();
			stored = AuthStore.ReadStoredApiKey("google-vertex", directory.FullName)!;
			Assert.Equal("project-2", stored.Environment["GOOGLE_CLOUD_PROJECT"]);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void EmptyKeyInputKeepsTheExistingSecret()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-key-panel-");
		try
		{
			AuthStore.SaveApiKey("deepseek", "existing-key", directory.FullName);
			var status = ProviderAuthService.Statuses(directory.FullName)
				.Single(item => item.Id == "deepseek");
			var panel = new ProviderKeyPanel(status, directory.FullName, (_, _) => { });
			panel.FillForSmoke(string.Empty);
			panel.SaveForSmoke();

			Assert.Equal("existing-key", AuthStore.ReadStoredApiKey("deepseek", directory.FullName)!.Key);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void ExplicitOverwriteReplacesOauthCredential()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-key-panel-");
		try
		{
			OAuthStore.Save("anthropic", new OAuthCredential
			{
				Access = "access",
				Refresh = "refresh",
				Expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
			}, directory.FullName);
			var status = ProviderAuthService.Statuses(directory.FullName)
				.Single(item => item.Id == "anthropic");
			Assert.True(status.OAuthConfigured);
			var panel = new ProviderKeyPanel(status, directory.FullName, (_, _) => { });
			panel.FillForSmoke("new-key");
			panel.SaveForSmoke(overwriteOAuth: true);

			Assert.Null(OAuthStore.Read("anthropic", directory.FullName));
			Assert.Equal("new-key", AuthStore.ReadStoredApiKey("anthropic", directory.FullName)!.Key);
		}
		finally { directory.Delete(recursive: true); }
	}
}
