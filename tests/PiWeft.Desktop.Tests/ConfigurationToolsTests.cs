using PiWeft.CodingAgent.Settings;
using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 设置页「工具与 Codemode」的语义：默认集、纯名字整体替换、+/- 增量、显式空选择，
/// 以及 codemode 子键的最小化回写。
/// </summary>
public sealed class ConfigurationToolsTests
{
	[Fact]
	public void ResolveFollowsTheRuntimeToolSemantics()
	{
		Assert.Equal(CodingAgentSettings.DefaultToolNames, ConfigurationTools.Resolve(null));
		Assert.Empty(ConfigurationTools.Resolve([]));
		Assert.Equal(new[] { "read", "grep" }, ConfigurationTools.Resolve(new[] { "read", "grep" }));

		// 只含增量：在默认集上增删。
		Assert.Equal(CodingAgentSettings.DefaultToolNames.Append("codemode"), ConfigurationTools.Resolve(new[] { "+codemode" }));
		Assert.Equal(CodingAgentSettings.DefaultToolNames.Where(name => name != "bash"), ConfigurationTools.Resolve(new[] { "-bash" }));

		// 纯名字出现即整体替换，其后的增量在该集合上应用。
		Assert.Equal(new[] { "read", "grep" }, ConfigurationTools.Resolve(new[] { "read", "-bash", "+grep" }));
	}

	[Fact]
	public void EncodingKeepsTheSmallestFormThatResolvesToTheSelection()
	{
		// 关掉一个默认工具写成增量；再打开则回到「删除键」。
		var withoutRead = ConfigurationTools.SetEnabled(null, null, "read", false);
		Assert.Equal(new[] { "-read" }, withoutRead);
		Assert.Null(ConfigurationTools.SetEnabled(null, withoutRead, "read", true));

		// 全部禁用后只开一个：必须写成纯名字，写成 +read 会把默认集带回来。
		var none = ConfigurationTools.Encode(null, null, []);
		Assert.Empty(none!);
		Assert.Equal(new[] { "read" }, ConfigurationTools.SetEnabled(null, none, "read", true));

		// 与改动无关的自定义条目原样保留，新工具按目录顺序插入。
		IReadOnlyList<string> custom = new[] { "read", "my-plugin" };
		var extended = ConfigurationTools.SetEnabled(null, custom, "bash", true);
		Assert.Equal(new[] { "read", "bash", "my-plugin" }, extended);
	}

	[Fact]
	public void ProjectLayerResolvesAgainstTheInheritedEntries()
	{
		IReadOnlyList<string> global = new[] { "read", "bash" };

		Assert.Equal(global, ConfigurationTools.ResolveInLayer(global, null));
		Assert.Equal(new[] { "read", "bash", "grep" }, ConfigurationTools.ResolveInLayer(global, new[] { "+grep" }));
		Assert.Equal(new[] { "read" }, ConfigurationTools.ResolveInLayer(global, new[] { "read" }));
		// 显式空列表是「全部禁用」，不能回退到全局或默认集。
		Assert.Empty(ConfigurationTools.ResolveInLayer(global, []));
		// 撤销增量后回到继承：删除项目键。
		Assert.Null(ConfigurationTools.SetEnabled(global, new[] { "+grep" }, "grep", false));
	}

	[Fact]
	public void CodemodePatchOnlyWritesChangedKeys()
	{
		var existing = JsonNode.Parse("""{"mode":"on","autoEnable":false}""")!;
		var patched = ConfigurationTools.PatchCodemode(existing, "only", null, modeChanged: true, inlineBudgetChanged: false)!;
		Assert.Equal("only", patched["mode"]!.GetValue<string>());
		Assert.False(patched["autoEnable"]!.GetValue<bool>());
		Assert.False(patched.ContainsKey("inlineBudget"));

		// 清空全部建模子键时删除整个 codemode 键，避免落盘 codemode: {}。
		Assert.Null(ConfigurationTools.PatchCodemode(JsonNode.Parse("""{"mode":"on"}"""), null, null, true, false));

		// 预算 0 是有效值（只列命名空间），不能被当成「继承」。
		var zero = ConfigurationTools.PatchCodemode(null, null, 0, modeChanged: false, inlineBudgetChanged: true)!;
		Assert.Equal(0, zero["inlineBudget"]!.GetValue<int>());
	}

	[Fact]
	public void CatalogMatchesTheRuntimeDeclaration()
	{
		Assert.Equal("read", ConfigurationTools.Catalog[0]);
		Assert.Contains("codemode", ConfigurationTools.Catalog);
		Assert.True(ConfigurationTools.IsExtension("codemode"));
		Assert.False(ConfigurationTools.IsExtension("read"));
		Assert.Equal(OperatingSystem.IsWindows(), ConfigurationTools.Catalog.Contains("powershell"));
	}
}
