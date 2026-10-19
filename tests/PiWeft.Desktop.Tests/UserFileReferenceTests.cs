using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>用户草稿/消息里的文件引用只做展示层识别：短标签、保留顺序，不误伤 URL 和代码。</summary>
public sealed class UserFileReferenceTests
{
	[Fact]
	public void QuotedAndUnquotedReferencesBecomeShortLabelsInOrder()
	{
		const string text = "先看 @\"src/App.cs\"，再看 @docs/guide.md 和 @\"/tmp/My Docs/spec.pdf\"。";
		var references = UserFileReferences.Parse(text);

		Assert.Equal(3, references.Count);
		Assert.Equal("App.cs", references[0].Label);
		Assert.Equal("guide.md", references[1].Label);
		Assert.Equal("spec.pdf", references[2].Label);
		Assert.Equal("/tmp/My Docs/spec.pdf", references[2].Path);
		Assert.False(references[2].IsDirectory);
	}

	[Fact]
	public void QuotedReferenceUnescapesBackslashesAndQuotes()
	{
		var references = UserFileReferences.Parse("@\"C:\\\\Users\\\\Me\\\\a\\\"b.txt\"");

		var reference = Assert.Single(references);
		Assert.Equal("C:\\Users\\Me\\a\"b.txt", reference.Path);
		Assert.Equal("a\"b.txt", reference.Label);
	}

	[Fact]
	public void DirectoryReferenceKeepsItsTrailingSlashInTheLabel()
	{
		var references = UserFileReferences.Parse("看 @docs/ 和 @\"src/components/\"。");
		Assert.Equal(2, references.Count);
		Assert.True(references[0].IsDirectory);
		Assert.Equal("docs/", references[0].Label);
		Assert.True(references[1].IsDirectory);
		Assert.Equal("components/", references[1].Label);
	}

	[Fact]
	public void UrlsInlineCodeAndFencedCodeAreNotReferences()
	{
		const string text = """
			行内 `@fake/inline.cs` 不解析；
			```ts
			import pkg from "@scope/pkg";
			const path = "@fake/file.txt";
			```
			https://example.com/@user/repo 也不解析；
			@scope/pkg 不是路径；
			@README.md 是文件。
			""";
		var reference = Assert.Single(UserFileReferences.Parse(text));
		Assert.Equal("README.md", reference.Label);
	}

	[Fact]
	public void UnquotedReferencesCanFollowChineseTextWithoutASpace()
	{
		var reference = Assert.Single(UserFileReferences.Parse("看@docs/。"));
		Assert.Equal("docs/", reference.Label);
		Assert.Empty(UserFileReferences.Parse("user@example.com"));
		Assert.Empty(UserFileReferences.Parse("看 @https://example.com/a"));
	}

}
