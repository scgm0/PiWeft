using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 仓内 `.gitattributes` 的审查层：后行覆盖前行、basename 与路径模式、`**`、生成物归测试。
public sealed class GitReviewRulesTests
{
	private const string Attributes = """
		# 仓库自己的约定
		*.g.cs piweft-review-layer=test
		generated/** piweft-review-layer=tests
		api/*.yaml linguist-generated
		src/Only.txt piweft-review-layer=interface
		*.txt piweft-review-layer=implementation
		src/Only.txt piweft-review-layer=test
		"docs/api note.md" piweft-review-layer=interface
		/root.txt piweft-review-layer=test
		file?.dat piweft-review-layer=test
		!skip-child/** piweft-review-layer=test
		""";

	[Fact]
	public void AttributesOverrideTheBuiltInLayers()
	{
		var rules = GitReviewRules.Parse(Attributes);

		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("deep/a.g.cs", rules));
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("generated/deep/x.cs", rules));
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("api/a.yaml", rules));
		// 后出现的行覆盖前面：src/Only.txt 先是接口，随后被改成测试。
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("src/Only.txt", rules));
		// 没有命中显式层，仍走内置规则（.txt 是 implementation）。
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("README.txt", rules));
		// 带空格的模式可以用引号包住。
		Assert.Equal(GitReviewLayer.Interface, GitReviewLayers.For("docs/api note.md", rules));
		// 以 / 开头锚定仓库根；不带 / 的按 basename。
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("root.txt", rules));
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("src/root.txt", rules));
		// ? 只吃一个非斜杠字符。
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("file1.dat", rules));
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("file10.dat", rules));
	}

	[Fact]
	public void NegatedAndUnknownAttributesDoNotMoveFiles()
	{
		var rules = GitReviewRules.Parse("""
			!skip-child/** piweft-review-layer=test
			*.weird piweft-review-layer=unknown
			""");

		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("skip-child/a.cs", rules));
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("a.weird", rules));
	}

	[Fact]
	public void UnsetAttributesFallBackToTheBuiltInRules()
	{
		var rules = GitReviewRules.Parse("""
			*.g.cs piweft-review-layer=test
			*.g.cs -piweft-review-layer
			api.json linguist-generated
			api.json -linguist-generated
			""");

		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("a.g.cs", rules));
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("api.json", rules));
	}

	[Fact]
	public void MissingAttributesUseTheBuiltInRules()
	{
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("src/App.cs", GitReviewRules.Empty));
		Assert.Equal(GitReviewLayer.Tests, GitReviewLayers.For("tests/AppTests.cs", GitReviewRules.Empty));
	}
}
