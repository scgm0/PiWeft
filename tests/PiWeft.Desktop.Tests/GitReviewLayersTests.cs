using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 审查层按路径分类：比较列表靠它把实现从测试与契约里剥出来。
/// 规则都是约定，必须钉住几个边界：`latest.cs` 不是测试，`Foo.Tests` 目录是。
public sealed class GitReviewLayersTests
{
	[Theory]
	[InlineData("src/App.cs", GitReviewLayer.Implementation)]
	[InlineData("README.md", GitReviewLayer.Implementation)]
	[InlineData("assets/icon.png", GitReviewLayer.Implementation)]
	[InlineData("src/latest.cs", GitReviewLayer.Implementation)]
	[InlineData("tests/AppTests.cs", GitReviewLayer.Tests)]
	[InlineData("src/PiWeft.Desktop.Tests/GitView.cs", GitReviewLayer.Tests)]
	[InlineData("__tests__/view.spec.ts", GitReviewLayer.Tests)]
	[InlineData("web/src/app.spec.tsx", GitReviewLayer.Tests)]
	[InlineData("pkg/client_test.go", GitReviewLayer.Tests)]
	[InlineData("spec/models/user_spec.rb", GitReviewLayer.Tests)]
	[InlineData("snapshots/a.snap", GitReviewLayer.Tests)]
	[InlineData("pnpm-lock.yaml", GitReviewLayer.Tests)]
	[InlineData("api/openapi.yaml", GitReviewLayer.Interface)]
	[InlineData("api/thing.proto", GitReviewLayer.Interface)]
	[InlineData("include/foo.h", GitReviewLayer.Interface)]
	[InlineData("web/package.json", GitReviewLayer.Interface)]
	[InlineData("src/PiWeft.Desktop.csproj", GitReviewLayer.Interface)]
	[InlineData("Directory.Build.props", GitReviewLayer.Interface)]
	[InlineData("types/index.d.ts", GitReviewLayer.Interface)]
	public void PathsMapToTheirLayer(string path, GitReviewLayer expected) =>
		Assert.Equal(expected, GitReviewLayers.For(path));

	[Fact]
	public void NullWindowShapesFallBackToImplementation()
	{
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For(string.Empty));
		Assert.Equal(GitReviewLayer.Implementation, GitReviewLayers.For("C:\\proj\\src\\App.cs"));
	}
}
