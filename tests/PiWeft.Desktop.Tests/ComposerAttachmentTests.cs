using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Platform;
using System.Reflection;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ComposerAttachmentTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-attachments-" + Guid.NewGuid().ToString("N"));
	public ComposerAttachmentTests() => Directory.CreateDirectory(_root);

	private SessionTab Tab(bool vision = true)
	{
		var model = FakeStreams.Model() with { Input = vision ? ["text", "image"] : ["text"] };
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root, Model = model, StreamFn = (model, _, _) => FakeStreams.Text(model, "ok"),
			Session = SessionManager.InMemory(_root), Persist = false, Skills = [],
		});
		return new SessionTab(session, [model], "test");
	}

	[Fact]
	public async Task AttachmentPreviewIsSeparateFromRemovalAndClosesWhenTheTabIsDisposed()
	{
		using var tab = Tab();
		var window = new Window().Content(tab.View);
		var path = Path.Combine(_root, "preview.png");
		File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII="));
		await tab.AddFilesAsync([path]);
		ImagePreviewTests.Click(Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.ImagePreviewOpen));
		Assert.Equal(1, window.OverlayLayer.Count);
		Assert.Contains(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.RemoveAttachment);
		tab.DisposeView();
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public async Task MixedDropInsertsReferencesAtCaretAndSendsImageBlocks()
	{
		using var tab = Tab();
		var text = Path.Combine(_root, "file with spaces.txt");
		File.WriteAllText(text, "text");
		var image = Path.Combine(_root, "photo.png");
		File.WriteAllBytes(image, [1, 2, 3]);
		var composer = Find<MultiLineTextBox>(tab.View).Single();
		composer.Text = "before after";
		composer.Select(6, 0);
		await tab.AddFilesAsync([text, _root, image, image]);
		Assert.Contains(ComposerDrop.Reference(text), composer.Text);
		Assert.Contains(ComposerDrop.Reference(_root), composer.Text);
		Assert.StartsWith("before ", composer.Text);
		Assert.EndsWith(" after", composer.Text);
		Assert.Equal(2, tab.ComposerReferenceCount);
		await tab.SendAsync(composer.Text);
		var message = Assert.IsType<UserMessage>(tab.Session.Session.BuildSessionContext().Messages.First());
		Assert.Single(message.Content.Blocks!.OfType<ImageContent>());
	}

	[Fact]
	public async Task ImageOnlyDraftCanBeSentFromButtonAction()
	{
		using var tab = Tab();
		var image = Path.Combine(_root, "photo.png");
		File.WriteAllBytes(image, [1, 2, 3]);
		await tab.AddFilesAsync([image]);
		tab.SendFromShortcut();
		await tab.PendingSend.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Contains(tab.Session.Session.BuildSessionContext().Messages, message => message is UserMessage user && user.Content.Blocks!.OfType<ImageContent>().Any());
	}

	[Fact]
	public async Task TextOnlyModelKeepsAttachmentsAndDraftInsteadOfSending()
	{
		using var tab = Tab(vision: false);
		var image = Path.Combine(_root, "photo.png");
		File.WriteAllBytes(image, [1, 2, 3]);
		await tab.AddFilesAsync([image]);
		await tab.SendAsync("inspect");
		Assert.Empty(tab.Session.Session.BuildSessionContext().Messages);
		Assert.Equal("inspect", Find<MultiLineTextBox>(tab.View).Single().Text);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.ImageModelRequired);
	}

	[Fact]
	public void ComposerNegotiatesCopyBeforeNativeDataIsAvailable()
	{
		using var tab = Tab();
		var composer = Find<MultiLineTextBox>(tab.View).Single();
		var target = composer.Parent;
		while (target is not null && target is not UIElement { AllowDrop: true }) target = target.Parent;
		var dropTarget = Assert.IsAssignableFrom<UIElement>(target);
		// X11 advertises the format during DragOver and supplies paths only after Drop.
		var data = new DataObject();
		data.SetStorageItems([]);
		var args = new DragEventArgs(data, default, default);
		RaiseDrag(dropTarget, "OnDragOver", args);
		Assert.True(args.Accepted);
		Assert.Equal(DragDropEffects.Copy, args.Effect);
	}

	[Fact]
	public void ConversationSurfaceAcceptsFilesAndInsertsThemIntoItsOwnDraft()
	{
		using var tab = Tab();
		var path = Path.Combine(_root, "文件 with spaces.txt");
		File.WriteAllText(path, "text");
		var data = new DataObject();
		data.SetStorageItems(new List<string> { path });
		var over = new DragEventArgs(data, default, default);
		Assert.True(tab.View.AllowDrop);
		RaiseDrag(tab.View, "OnDragOver", over);
		Assert.True(over.Accepted);
		var drop = new DragEventArgs(data, default, default);
		RaiseDrag(tab.View, "OnDrop", drop);
		Assert.True(drop.Accepted);
		Assert.True(drop.Handled);
		Assert.Contains(ComposerDrop.Reference(path), Find<MultiLineTextBox>(tab.View).Single().Text);
		Assert.Empty(tab.Session.Session.BuildSessionContext().Messages);
	}

	[Fact]
	public void UnsupportedDragAndMoveOnlySourcesAreRejected()
	{
		using var tab = Tab();
		var unsupported = new DataObject();
		unsupported.SetData("application/x-unknown", new byte[] { 1 });
		var args = new DragEventArgs(unsupported, default, default);
		RaiseDrag(tab.View, "OnDragOver", args);
		Assert.False(args.Accepted);
		Assert.Equal(DragDropEffects.None, args.Effect);
		var files = new DataObject();
		files.SetStorageItems([_root]);
		args = new DragEventArgs(files, default, default, DragDropEffects.Move);
		RaiseDrag(tab.View, "OnDragOver", args);
		Assert.False(args.Accepted);
	}

	private static void RaiseDrag(UIElement target, string name, DragEventArgs args) =>
		typeof(UIElement).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [args]);

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
