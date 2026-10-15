using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Resources;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ClipboardImageTests : IDisposable
{
	private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j7XcAAAAASUVORK5CYII=";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-clipboard-" + Guid.NewGuid().ToString("N"));
	private readonly IClipboardImages? _previous = ClipboardImages.Service;

	public ClipboardImageTests() => Directory.CreateDirectory(_root);

	private SessionTab Tab()
	{
		var model = FakeStreams.Model() with { Input = ["text", "image"] };
		var session = new CodingAgentSession(new CodingAgentSessionOptions
		{
			Cwd = _root, Model = model, StreamFn = (model, _, _) => FakeStreams.Text(model, "ok"),
			Session = SessionManager.InMemory(_root), Persist = false, Skills = [],
		});
		return new SessionTab(session, [model], "test");
	}

	[Fact]
	public async Task PastingAnImageAttachesItAndLeavesTheDraftAlone()
	{
		using var tab = Tab();
		var composer = Find<MultiLineTextBox>(tab.View).Single();
		composer.Text = "看看这张图";
		ClipboardImages.Service = new FakeClipboardImages { Image = true };

		var key = new KeyEventArgs(Key.V, 0, ModifierKeys.Control);
		Press(composer, key);
		Assert.True(key.Handled);
		await tab.PendingPaste;

		Assert.Equal("看看这张图", composer.Text);
		Assert.Contains(Find<Button>(tab.View), button => HoverHints.Text(button) == Strings.RemoveAttachment);
	}

	[Fact]
	public void ClipboardWithoutImagesKeepsTheDefaultTextPastePath()
	{
		using var tab = Tab();
		var composer = Find<MultiLineTextBox>(tab.View).Single();
		ClipboardImages.Service = new FakeClipboardImages { Image = false };

		var key = new KeyEventArgs(Key.V, 0, ModifierKeys.Control);
		Press(composer, key);
		Assert.False(key.Handled);

		// 没有平台实现时同样不能吞掉按键，否则文本粘贴会一起失效。
		ClipboardImages.Service = null;
		key = new KeyEventArgs(Key.V, 0, ModifierKeys.Control);
		Press(composer, key);
		Assert.False(key.Handled);
		Assert.Same(Task.CompletedTask, tab.PendingPaste);
	}

	[Fact]
	public async Task FailedReadReportsInsteadOfSwallowingThePaste()
	{
		using var tab = Tab();
		var composer = Find<MultiLineTextBox>(tab.View).Single();
		ClipboardImages.Service = new FakeClipboardImages { Image = true, Result = [] };

		Press(composer, new KeyEventArgs(Key.V, 0, ModifierKeys.Control));
		await tab.PendingPaste;

		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.ClipboardImageUnavailable);
	}

	[Fact]
	public async Task CopyingAPreviewedImageWritesTheOriginalBytesToTheClipboard()
	{
		using var tab = Tab();
		var window = new Window().Content(tab.View);
		var service = new FakeClipboardImages();
		ClipboardImages.Service = service;
		var path = Path.Combine(_root, "shot.png");
		var png = Convert.FromBase64String(Png);
		File.WriteAllBytes(path, png);
		await tab.AddFilesAsync([path]);

		ImagePreviewTests.Click(Find<Button>(tab.View).Single(button => HoverHints.Text(button) == Strings.ImagePreviewOpen));
		Assert.Equal(1, window.OverlayLayer.Count);
		ImagePreviewTests.Click(Find<Button>(tab.ImagePreview.View).Single(button => HoverHints.Text(button) == Strings.CopyImage));

		var written = Assert.Single(service.Written);
		Assert.Equal("image/png", written.MimeType);
		Assert.Equal(png, written.Data);
		Assert.Contains(Find<TextBlock>(tab.View), block => block.Text == Strings.Copied);
	}

	[Fact]
	public void ImagePreviewWithoutRecordedBytesOffersNoCopyAction()
	{
		var window = new Window();
		var preview = new ImagePreview();
		// 直接构造图片源，绕过登记原始数据的入口：没有可写回剪贴板的字节时不该出现注定失败的按钮。
		var source = ImageSource.FromBytes(PngCodec.Encode(PixelImage.Create(2, 2)));
		preview.Show(window, source);
		Assert.DoesNotContain(Find<Button>(preview.View),
			button => button.IsVisible && HoverHints.Text(button) == Strings.CopyImage);
		preview.Hide();
	}

	[Fact]
	public async Task LinuxBackendRoundTripsImageBytesThroughTheRealClipboard()
	{
		if (ClipboardImages.Create() is not { } service) return;
		using var owner = service;
		var png = Convert.FromBase64String(Png);
		Assert.True(await service.TrySetImageAsync("image/png", png));

		IReadOnlyList<ClipboardAttachment> attachments = [];
		for (var attempt = 0; attempt < 40; attempt++)
		{
			if (service.HasImage()) attachments = await service.ReadAsync();
			if (attachments.Count > 0) break;
			await Task.Delay(25);
		}

		var attachment = Assert.Single(attachments);
		Assert.Equal("image/png", attachment.Content.MimeType);
		Assert.Equal(png, Convert.FromBase64String(attachment.Content.Data));
		Assert.EndsWith(".png", attachment.Name);
	}

	private static void Press(UIElement element, KeyEventArgs args) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [args]);

	public void Dispose()
	{
		ClipboardImages.Service = _previous;
		Directory.Delete(_root, recursive: true);
	}

	private sealed class FakeClipboardImages : IClipboardImages
	{
		public bool Image { get; init; }

		/// <summary>ReadAsync 的返回值；未指定时给一张默认 PNG。</summary>
		public IReadOnlyList<ClipboardAttachment>? Result { get; init; }

		public List<(string MimeType, byte[] Data)> Written { get; } = [];

		public bool HasImage() => Image;

		public Task<IReadOnlyList<ClipboardAttachment>> ReadAsync() => Task.FromResult(Result ?? Default());

		public Task<bool> TrySetImageAsync(string mimeType, byte[] data)
		{
			Written.Add((mimeType, data));
			return Task.FromResult(true);
		}

		public void Dispose() { }

		private static IReadOnlyList<ClipboardAttachment> Default() =>
			[new ClipboardAttachment("pasted-00000000.png", new ImageContent { Data = Png, MimeType = "image/png" })];
	}
}
