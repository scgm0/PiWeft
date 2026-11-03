using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ProviderAuthViewTests
{
	[Fact]
	public async Task AuthPageShowsSubscriptionCardsAndProtectsDraft()
	{
		var directory = Directory.CreateTempSubdirectory("piweft-auth-view-");
		try
		{
			await File.WriteAllTextAsync(Path.Combine(directory.FullName, "auth.json"), """
				{"anthropic":{"type":"oauth","access":"access","refresh":"refresh","expires":1924992000,"accountId":"demo"},
				 "deepseek":{"type":"api_key","key":"sk-demo"}}
				""");
			var view = new ConfigurationView(directory.FullName, directory.FullName, _ => { });
			Assert.True(await view.OpenAsync(ConfigFile.Auth));
			Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.AuthSubscriptionTitle);
			Assert.Contains(Find<TextBlock>(view.View), block => block.Text.Contains("Anthropic", StringComparison.Ordinal));
			Assert.Contains(Find<TextBlock>(view.View), block => block.Text.Contains("deepseek", StringComparison.Ordinal));
			view.View.Measure(new Size(900, 600));
			Assert.True(view.View.DesiredSize.Width > 0);
			UiLifetime.Release(view.View);
		}
		finally { directory.Delete(recursive: true); }
	}

	[Fact]
	public void LoginPanelFitsNarrowWindowsWithoutNetwork()
	{
		var provider = ProviderAuth.Find("xai")!;
		var panel = new ProviderLoginPanel(provider, Path.GetTempPath());
		panel.View.IsVisible = true;
		var scroll = Find<ScrollViewer>(panel.View).First();
		panel.View.Measure(new Size(600, 420));
		panel.View.Arrange(new Rect(0, 0, 600, 420));
		Assert.True(scroll.ViewportHeight > 0);
		Assert.True(scroll.Bounds.Bottom <= 420);
		Assert.True(scroll.Bounds.Right <= 600);
		UiLifetime.Release(panel.View);
	}
}
