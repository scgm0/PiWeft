using System.Text.Json.Nodes;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Settings;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// settings.extensions 的写回与设置页开关：只改数组、保留其它键，
/// 内置扩展写成 builtin: 条目，文件扩展写成绝对路径。
[Collection("mewui-controls")]
public sealed class ExtensionSettingsTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-extension-settings-" + Guid.NewGuid().ToString("N")[..8]);

	public ExtensionSettingsTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "agent"));
		Directory.CreateDirectory(Path.Combine(_root, "project"));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	private string AgentDir => Path.Combine(_root, "agent");
	private string Cwd => Path.Combine(_root, "project");
	private string SettingsPath => CodingAgentSettings.GlobalPath(AgentDir);
	private SettingsView Create() => new(
		() => new SettingsSnapshot
		{
			AgentDir = AgentDir,
			Cwd = Cwd,
			ModelCount = 0,
			Tools = [],
			Skills = [],
			McpServers = [],
			Resources = PiWeft.CodingAgent.Resources.ResourceSnapshot.Empty,
		});

	[Fact]
	public void DesktopBuiltinCatalogMatchesPiDeckEntries()
	{
		var all = DesktopExtensions.All;

		// PiDeck 的 14 个内置项里只有 10 个已移植；未移植的不列出，避免看起来在生效。
		Assert.Equal(10, all.Count);
		Assert.All(all, static extension => Assert.DoesNotContain(".ts", extension.Name, StringComparison.Ordinal));
		Assert.Contains(all, static extension => extension.Name == "pi-deck-request-size-recovery");
		Assert.DoesNotContain(all, static extension => extension.Name == "pi-deck-model-trace");
	}

	[Fact]
	public void WriterChangesOnlyTheExtensionsArray()
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(SettingsPath, """{"defaultModel":"test-model","extensions":["ext/keep.js"]}""");
		var path = Path.GetFullPath(Path.Combine(AgentDir, "extensions", "off.js"));
		var rule = path.Replace('\\', '/');

		Assert.True(ExtensionSettings.TrySetEnabled(AgentDir, path, false, out var error), error);
		var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
		Assert.Equal("test-model", root["defaultModel"]!.GetValue<string>());
		Assert.Equal(new[] { "ext/keep.js", "-" + rule }, root["extensions"]!.AsArray().Select(node => node!.GetValue<string>()));

		Assert.True(ExtensionSettings.TrySetBuiltinEnabled(AgentDir, "mcp", false, out error), error);
		root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
		Assert.Equal(
			new[] { "ext/keep.js", "-" + rule, "-builtin:mcp" },
			root["extensions"]!.AsArray().Select(node => node!.GetValue<string>()));

		// 再打开只清理精确负项，不碰其它路径。
		Assert.True(ExtensionSettings.TrySetBuiltinEnabled(AgentDir, "mcp", true, out error), error);
		root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
		Assert.Equal(
			new[] { "ext/keep.js", "-" + rule, "+builtin:mcp" },
			root["extensions"]!.AsArray().Select(node => node!.GetValue<string>()));
	}

	[Fact]
	public async Task SettingsPageTogglesBuiltinAndDiscoveredExtensions()
	{
		var extensions = Path.Combine(AgentDir, "extensions");
		Directory.CreateDirectory(extensions);
		var extensionPath = Path.Combine(extensions, "good.js");
		File.WriteAllText(extensionPath, "module.exports = function (pi) {};");

		var view = Create();
		await view.SelectSectionAsync("extensions");
		ConversationPerformanceTests.Layout(view.View, 800, 720);

		var toggles = Find<ToggleSwitch>(view.View).ToArray();
		// 4 个 Pi 内置 + 10 个桌面内置 + 1 个刚发现的文件扩展。
		Assert.Equal(15, toggles.Length);
		var texts = Find<TextBlock>(view.View).Select(static block => block.Text ?? string.Empty).ToArray();
		Assert.Contains("pi-deck-ask-question", texts);
		Assert.DoesNotContain(texts, static text => text.Contains(".ts", StringComparison.Ordinal));

		// 前四行是 builtin:mcp / llama.cpp / codemode / tool-search，最后一行的文件扩展可写盘。
		toggles[0].IsChecked = false;
		var entries = JsonNode.Parse(File.ReadAllText(SettingsPath))!["extensions"]!.AsArray()
			.Select(node => node!.GetValue<string>()).ToArray();
		Assert.Contains("-builtin:mcp", entries);

		toggles = Find<ToggleSwitch>(view.View).ToArray();
		Assert.Equal(15, toggles.Length);
		toggles[14].IsChecked = false;
		entries = JsonNode.Parse(File.ReadAllText(SettingsPath))!["extensions"]!.AsArray()
			.Select(node => node!.GetValue<string>()).ToArray();
		Assert.Contains("-" + Path.GetFullPath(extensionPath).Replace('\\', '/'), entries);
	}
}
