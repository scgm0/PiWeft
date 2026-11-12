using PiWeft.Ai;
using PiWeft.CodingAgent.Settings;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// pi 1.5 设置项的读取与写回。
public sealed class SettingsParityTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-settingsparity-" + Guid.NewGuid().ToString("N")[..8]);

	public SettingsParityTests() => Directory.CreateDirectory(_root);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private string AgentDir => Path.Combine(_root, "agent");
	private string Project => Path.Combine(_root, "project");

	private void WriteGlobal(string json)
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "settings.json"), json);
	}

	[Fact]
	public void CacheWarmingDefaultsToIdle()
	{
		WriteGlobal("{}");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);

		Assert.Equal("idle", settings.CacheWarming);
	}

	[Fact]
	public void CompactionModelOverridesApplyPerModel()
	{
		WriteGlobal("""
			{
				"compaction": {
					"reserveTokens": 16384,
					"keepRecentTokens": 20000,
					"modelOverrides": {
						"anthropic/claude-opus-5-5": { "reserveTokens": 400000, "keepRecentTokens": 30000 }
					}
				}
			}
			""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);
		var model = new Model { Provider = "anthropic", Id = "claude-opus-5-5" };
		var effective = settings.Compaction.ForModel(model);

		Assert.Equal(400000, effective.ReserveTokens);
		Assert.Equal(30000, effective.KeepRecentTokens);
		Assert.Equal(16384, settings.Compaction.ForModel(new Model { Provider = "anthropic", Id = "other" }).ReserveTokens);

		Directory.CreateDirectory(Path.Combine(Project, ".pi"));
		CodingAgentSettings.SaveProject(Project, settings);
		var reloaded = CodingAgentSettings.Load(Project, AgentDir, includeProject: true);
		Assert.Equal(400000, reloaded.Compaction.ForModel(model).ReserveTokens);
	}

	[Fact]
	public void ImagesAutoResizeIsReadWrittenAndMerged()
	{
		WriteGlobal("""{"images":{"autoResize":false,"blockImages":true}}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);
		Assert.False(settings.Images.AutoResize);
		Assert.True(settings.Images.BlockImages);

		var file = CodingAgentSettings.ToFile(settings);
		Assert.False(file.Images!.AutoResize);
		Assert.True(file.Images.BlockImages);

		Directory.CreateDirectory(Path.Combine(Project, ".pi"));
		CodingAgentSettings.SaveProject(Project, settings);
		var written = System.Text.Json.Nodes.JsonNode.Parse(
			File.ReadAllText(Path.Combine(Project, ".pi", "settings.json")))!;
		Assert.False(written["images"]!["autoResize"]!.GetValue<bool>());
		Assert.True(written["images"]!["blockImages"]!.GetValue<bool>());

		// 项目覆盖优先；默认档（自动缩放开、不拦图）不写 images。
		File.WriteAllText(Path.Combine(Project, ".pi", "settings.json"), """{"images":{"autoResize":true}}""");
		var merged = CodingAgentSettings.Load(Project, AgentDir, includeProject: true);
		Assert.True(merged.Images.AutoResize);
		Assert.True(merged.Images.BlockImages);
		Assert.Null(CodingAgentSettings.ToFile(merged with { Images = new ImagesSettings() }).Images);
	}

	[Fact]
	public void ThinkingBudgetsAndImageBlockAreReadAndWritten()
	{
		WriteGlobal("""{"thinkingBudgets":{"low":1234,"high":4321},"images":{"blockImages":true}}""");

		var settings = CodingAgentSettings.Load(Project, AgentDir, includeProject: false);
		Assert.Equal(1234, settings.ThinkingBudgets["low"]);
		Assert.True(settings.Images.BlockImages);

		var file = CodingAgentSettings.ToFile(settings);
		Assert.Equal(4321, file.ThinkingBudgets!["high"]);
		Assert.True(file.Images!.BlockImages);

		Directory.CreateDirectory(Path.Combine(Project, ".pi"));
		CodingAgentSettings.SaveProject(Project, settings);
		var written = File.ReadAllText(Path.Combine(Project, ".pi", "settings.json"));
		Assert.Contains("thinkingBudgets", written, StringComparison.Ordinal);
		Assert.Contains("blockImages", written, StringComparison.Ordinal);
	}
}
