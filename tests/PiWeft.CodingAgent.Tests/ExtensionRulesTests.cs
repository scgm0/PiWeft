using PiWeft.CodingAgent.Extensions;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// settings.extensions 的过滤规则：显式路径保留，精确 +/- 规则可往返，
/// 全局按 pi 的「排除 → 包含 → 排除」求值，项目层最后一条匹配覆盖。
public sealed class ExtensionRulesTests
{
	[Fact]
	public void ExactRulesToggleWithoutDroppingPlainEntries()
	{
		IReadOnlyList<string> entries = ["/ext/keep.js", "-/ext/x.js", "+/ext/x.js", "+builtin:mcp"];

		var disabled = ExtensionRules.SetEnabled(entries, "/ext/x.js", false, "/");
		Assert.Equal(new[] { "/ext/keep.js", "+builtin:mcp", "-/ext/x.js" }, disabled);

		var enabled = ExtensionRules.SetEnabled(disabled, "/ext/x.js", true, "/");
		Assert.Equal(new[] { "/ext/keep.js", "+builtin:mcp", "+/ext/x.js" }, enabled);
	}

	[Fact]
	public void BuiltinTogglesUseTheBuiltinSpecifier()
	{
		var disabled = ExtensionRules.SetBuiltinEnabled([], "mcp", false);
		Assert.Equal(new[] { "-builtin:mcp" }, disabled);

		var enabled = ExtensionRules.SetBuiltinEnabled(disabled, "mcp", true);
		Assert.Equal(new[] { "+builtin:mcp" }, enabled);
	}

	[Fact]
	public void GlobalRulesFollowExcludeIncludeExcludeOrder()
	{
		var rules = new ExtensionRules(["!/ext/**", "+/ext/x.js", "-/ext/y.js"], []);

		Assert.False(rules.IsEnabled("/ext/z.js"));
		Assert.True(rules.IsEnabled("/ext/x.js"));
		Assert.False(rules.IsEnabled("/ext/y.js"));
		Assert.True(rules.IsEnabled("/other/x.js"));
	}

	[Fact]
	public void ProjectRulesUseTheLastMatchingEntry()
	{
		var rules = new ExtensionRules([], ["!/ext/**", "+/ext/x.js", "-/ext/x.js", "+builtin:codemode"]);

		Assert.False(rules.IsEnabled("/ext/x.js"));
		Assert.False(rules.IsEnabled("/ext/other.js"));
		Assert.True(rules.IsBuiltinEnabled("codemode"));
	}
}
