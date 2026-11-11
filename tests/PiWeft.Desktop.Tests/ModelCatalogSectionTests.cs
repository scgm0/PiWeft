using System.Security.Cryptography;
using System.Text;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Models;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 设置 → 模型目录分区：状态行、自动更新开关与按钮可用性都来自覆盖层文件。
[Collection("mewui-controls")]
public sealed class ModelCatalogSectionTests : IDisposable
{
	private const string Catalog = """
		{"anthropic":[{"id":"claude-x","name":"Claude X","api":"anthropic-messages","provider":"anthropic","contextWindow":200000,"maxTokens":8192}]}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-model-catalog-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;

	public ModelCatalogSectionTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		Directory.CreateDirectory(_agentDir);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public async Task WithoutOverlayShowsBuiltinNowAndNoBackup()
	{
		var view = Create();
		await view.SelectSectionAsync("catalog");

		var texts = Texts(view);
		Assert.Null(view.LastSectionError);
		Assert.Contains(Strings.ModelCatalogDescription, texts);
		Assert.Contains(Strings.ModelCatalogSourceValue, texts);
		Assert.Contains(texts, static text => text.Contains("个供应商", StringComparison.Ordinal));
		Assert.Contains(Strings.ModelCatalogNone, texts);
		Assert.Contains(Strings.ModelCatalogAutoUpdate, texts);

		var restore = Button(view, Strings.ModelCatalogRestore);
		var previous = Button(view, Strings.ModelCatalogRestorePrevious);
		Assert.False(restore.IsEnabled);
		Assert.False(previous.IsEnabled);
	}

	[Fact]
	public async Task OverlayShowsVersionAndBackupEnablesRestore()
	{
		WriteOverlay(backup: false);
		var view = Create();
		await view.SelectSectionAsync("catalog");

		var texts = Texts(view);
		Assert.Null(view.LastSectionError);
		Assert.Contains(texts, static text => text.Contains("1.5.2", StringComparison.Ordinal));
		Assert.Contains(texts, static text => text.Contains("1 个供应商 · 1 条模型", StringComparison.Ordinal));
		Assert.True(Button(view, Strings.ModelCatalogRestore).IsEnabled);
		Assert.False(Button(view, Strings.ModelCatalogRestorePrevious).IsEnabled);

		WriteOverlay(backup: true);
		view = Create();
		await view.SelectSectionAsync("catalog");
		Assert.Contains(Strings.ModelCatalogHasBackup, Texts(view));
		Assert.True(Button(view, Strings.ModelCatalogRestorePrevious).IsEnabled);
	}

	[Fact]
	public async Task AutoUpdateSwitchFollowsThePreference()
	{
		File.WriteAllText(DesktopPreference.Path(_agentDir), """{"modelCatalogAutoUpdate":false}""");
		var view = Create();
		await view.SelectSectionAsync("catalog");

		var toggle = Find<CheckBox>(view.View).Single();
		Assert.False(toggle.IsChecked);
	}

	private SettingsView Create() => new(
		() => new SettingsSnapshot
		{
			AgentDir = _agentDir, Cwd = _root, ModelCount = 0,
			Tools = [], Skills = [], McpServers = [], Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
		});

	private void WriteOverlay(bool backup)
	{
		var bytes = Encoding.UTF8.GetBytes(Catalog);
		var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
		var meta = $$"""
			{"digest":"{{digest}}","entryCount":1,"providerCount":1,"fetchedAt":"2026-11-10T12:05:37+00:00","source":"pi-ai","packageVersion":"1.5.2"}
			""";
		File.WriteAllBytes(ModelCatalogFiles.CatalogPath(_agentDir), bytes);
		File.WriteAllText(ModelCatalogFiles.MetaPath(_agentDir), meta);

		if (backup)
		{
			File.Copy(ModelCatalogFiles.CatalogPath(_agentDir), ModelCatalogFiles.CatalogBackupPath(_agentDir), true);
			File.Copy(ModelCatalogFiles.MetaPath(_agentDir), ModelCatalogFiles.MetaBackupPath(_agentDir), true);
		}
	}

	private static Button Button(SettingsView view, string text) =>
		Find<Button>(view.View).Single(button => ButtonText(button).Contains(text, StringComparison.Ordinal));

	private static string ButtonText(Button button) =>
		button.Content is Element content
			? string.Join(" ", Find<TextBlock>(content).Select(static block => block.Text ?? string.Empty))
			: string.Empty;

	private static IReadOnlyList<string> Texts(SettingsView view) =>
		Find<TextBlock>(view.View).Select(static block => block.Text ?? string.Empty).ToList();
}
