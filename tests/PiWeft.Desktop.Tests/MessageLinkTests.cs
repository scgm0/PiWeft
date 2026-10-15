using System.Reflection;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Markdown;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class MessageLinkTests
{
	[Theory]
	[InlineData("src/file.cs", "src/file.cs", null, null)]
	[InlineData("./src/file.cs:12", "src/file.cs", 12, null)]
	[InlineData("src/file.cs:12:3", "src/file.cs", 12, 3)]
	[InlineData("file.cs:12", "file.cs", 12, null)]
	[InlineData("src/file.cs#L12C3", "src/file.cs", 12, 3)]
	[InlineData("src/file%20name.cs", "src/file name.cs", null, null)]
	public void RelativePathsResolveAgainstTheSessionDirectory(string url, string path, int? line, int? column)
	{
		var cwd = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "message-link-project"));
		var target = MessageLinkTarget.Resolve(url, cwd);
		Assert.NotNull(target);
		Assert.False(target.IsWeb);
		Assert.Equal(Path.GetFullPath(path, cwd), target.Target);
		Assert.Equal(line, target.Line);
		Assert.Equal(column, target.Column);
	}

	[Fact]
	public void AbsoluteFileUrisAndHomeRelativePathsAreLocalFiles()
	{
		var file = Path.Combine(Path.GetTempPath(), "message file.cs");
		var cwd = Environment.CurrentDirectory;
		Assert.Equal(file, MessageLinkTarget.Resolve(file, cwd)!.Target);
		Assert.Equal(file, MessageLinkTarget.Resolve(new Uri(file).AbsoluteUri, cwd)!.Target);
		Assert.Equal(file, MessageLinkTarget.Resolve(new Uri(file).AbsoluteUri + "#L20", cwd)!.Target);
		Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "file.cs"),
			MessageLinkTarget.Resolve("~/file.cs", cwd)!.Target);
	}

	[Theory]
	[InlineData("https://example.com/file?q=1#part")]
	[InlineData("http://localhost:8000")]
	public void HttpLinksStillOpenAsWebLinks(string url)
	{
		Assert.True(MessageLinkTarget.Resolve(url, Environment.CurrentDirectory)!.IsWeb);
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,test")]
	[InlineData("file://remote-server/share/file.cs")]
	[InlineData("#section")]
	[InlineData("invalid\npath")]
	public void UnsafeSchemesRemoteSharesAndDocumentAnchorsNeverStartAProcess(string url)
	{
		Assert.Null(MessageLinkTarget.Resolve(url, Environment.CurrentDirectory));
	}

	[Fact]
	public void MissingFilesProduceVisibleFeedbackRatherThanSilentlyIgnoringTheClick()
	{
		string? notice = null;
		var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.cs");
		Assert.False(Shell.OpenMessageLink(file, Environment.CurrentDirectory, text => notice = text));
		Assert.Equal(Strings.FileMissing(file), notice);
	}
}

[Collection("mewui-controls")]
public sealed class MarkdownLinkInteractionTests
{
	[Fact]
	public void LocalMarkdownLinksInvokeTheSameOpenHandlerAsWebLinks()
	{
		var opened = new List<string>();
		using var focus = new TextFocusScrollGuard(new ScrollViewer());
		var view = new AssistantMessageView(new ChatEntry
		{
			Role = ChatRole.Assistant, Text = "[file](src/file.cs:12) [web](https://example.com)",
		}, _ => { }, focus, openLink: opened.Add);
		var markdown = VisualTree.Find<MarkdownPresenter>(view.View).Single();
		// Invoke the framework link event using its real event arguments, without launching an editor.
		var eventField = typeof(MarkdownPresenter).GetField("LinkRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
		var handler = Assert.IsType<Action<MarkdownLinkRequestedEventArgs>>(eventField.GetValue(markdown));
		foreach (var url in new[] { "src/file.cs:12", "https://example.com" })
		{
			var args = (MarkdownLinkRequestedEventArgs)Activator.CreateInstance(typeof(MarkdownLinkRequestedEventArgs),
				BindingFlags.Instance | BindingFlags.NonPublic, null, [url, null, null, 0, 1], null)!;
			handler(args);
		}
		Assert.Equal(new[] { "src/file.cs:12", "https://example.com" }, opened);
		UiLifetime.Release(view.View);
	}
}
