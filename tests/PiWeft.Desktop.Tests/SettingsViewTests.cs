using Xunit;

namespace PiWeft.Desktop.Tests;

/// 设置搜索的页面级行为：查询期间结果替代内容，选中后清空搜索并切到目标分区。
[Collection("mewui-controls")]
public sealed class SettingsViewTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(), "piweft-settings-search-" + Guid.NewGuid().ToString("N"));

	private string AgentDir => Path.Combine(_root, "agent");

	private string Cwd => Path.Combine(_root, "project");

	public SettingsViewTests()
	{
		Directory.CreateDirectory(AgentDir);
		Directory.CreateDirectory(Cwd);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 临时目录清理失败不影响断言。
		}
	}

	[Fact]
	public async Task SearchNavigatesToSystemSectionAndClearsQuery()
	{
		var view = CreateView();

		view.SearchForSmoke(Strings.ConfigAppearance);
		Assert.True(view.SearchResultCount > 0);

		await view.SelectSearchResultForTest(0);

		Assert.Equal("appearance", view.CurrentSection);
		Assert.Equal(0, view.SearchResultCount);
		Assert.False(view.IsConfigMode);
	}

	[Fact]
	public void ResetSearchClearsResults()
	{
		var view = CreateView();
		view.SearchForSmoke(Strings.ConfigAppearance);
		Assert.True(view.SearchResultCount > 0);

		view.ResetSearch();

		Assert.Equal(0, view.SearchResultCount);
	}

	[Fact]
	public async Task SearchNavigatesToConfigSectionAndSwitchesMode()
	{
		var view = CreateView();

		view.SearchForSmoke("认证");
		Assert.True(view.SearchResultCount > 0);

		await view.SelectSearchResultForTest(0);

		Assert.True(view.IsConfigMode);
		Assert.Equal("auth", view.CurrentSection);
		Assert.Equal(0, view.SearchResultCount);
	}

	private SettingsView CreateView()
	{
		var snapshot = new SettingsSnapshot
		{
			AgentDir = AgentDir,
			Cwd = Cwd,
			ModelCount = 0,
			Tools = [],
			Skills = [],
			McpServers = [],
		};
		return new SettingsView(() => snapshot);
	}
}
