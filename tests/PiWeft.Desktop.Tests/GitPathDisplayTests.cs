using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// 行名显示边界：未跟踪的目录条目以 `/` 结尾，按最后斜杠切会切出空串。
public sealed class GitPathDisplayTests
{
	[Theory]
	[InlineData("src/App.cs", "App.cs")]
	[InlineData("packages/", "packages")]
	[InlineData("dir/a.txt", "a.txt")]
	[InlineData("", "")]
	[InlineData("C:\\proj\\main.cs", "main.cs")]
	public void CommitFileNamesKeepTheLastSegment(string path, string expected)
	{
		Assert.Equal(expected, new GitCommitFile("?", path, null).FileName);
		Assert.Equal(expected, new GitChange(" ", "M", path).FileName);
	}
}
