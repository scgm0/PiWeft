using Aprillz.MewUI.Platform;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class ComposerDropTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-drop-" + Guid.NewGuid().ToString("N"));
	public ComposerDropTests() => Directory.CreateDirectory(_root);

	[Fact]
	public void StorageItemsAcceptFilesAndDirectoriesAndDeduplicate()
	{
		var path = Path.Combine(_root, "some file.txt");
		File.WriteAllText(path, "text");
		var data = new DataObject();
		data.SetStorageItems([path, _root, path, Path.Combine(_root, "missing")]);
		Assert.Equal(new[] { path, _root }, ComposerDrop.Paths(data, _root));
		Assert.Equal("@\"" + path.Replace("\\", "\\\\") + "\"", ComposerDrop.Reference(path));
	}

	[Fact]
	public void UriListDecodesSpacesAndIgnoresRemoteUrls()
	{
		var path = Path.Combine(_root, "some file.txt");
		File.WriteAllText(path, "text");
		var data = new DataObject();
		data.SetData(DataFormats.Uris, new string[] { new Uri(path).AbsoluteUri, "https://example.com/image.png" });
		Assert.Equal(new[] { path }, ComposerDrop.Paths(data, _root));
	}

	[Fact]
	public void TextPathsSupportRelativeQuotedAndMultilinePaths()
	{
		File.WriteAllText(Path.Combine(_root, "a.txt"), "text");
		var data = new DataObject();
		data.SetText("\"a.txt\"\r\n#comment\r\n'" + _root + "'\nhttps://example.com\nnot a file");
		Assert.Equal(new[] { Path.Combine(_root, "a.txt"), _root }, ComposerDrop.Paths(data, _root));
	}

	[Fact]
	public async Task ImageFileIsEncodedWithItsMimeType()
	{
		var path = Path.Combine(_root, "photo.JPG");
		File.WriteAllBytes(path, [1, 2, 3]);
		var image = await ComposerDrop.ReadImageAsync(path);
		Assert.Equal("image/jpeg", image.MimeType);
		Assert.Equal("AQID", image.Data);
		Assert.False(ComposerDrop.IsImage("file.pdf"));
	}

	[Fact]
	public async Task OversizedImagesAreRejectedBeforeLoading()
	{
		var path = Path.Combine(_root, "large.png");
		using (var stream = File.Create(path)) stream.SetLength(ComposerDrop.MaxImageBytes + 1);
		await Assert.ThrowsAsync<IOException>(() => ComposerDrop.ReadImageAsync(path));
	}

	[Fact]
	public void NativeImageBytesAreSnapshottedWhileTheDragIsAlive()
	{
		var bytes = new byte[] { 1, 2, 3 };
		var data = new DataObject();
		data.SetData("image/png", bytes);
		var image = ComposerDrop.RawImage(data)!.Value;
		bytes[0] = 255;
		Assert.EndsWith(".png", image.Path);
		Assert.Equal("image/png", image.Content.MimeType);
		Assert.Equal("AQID", image.Content.Data);
	}

	[Fact]
	public void OversizedNativeImageBytesAreRejected()
	{
		var data = new DataObject();
		data.SetData("image/png", new byte[ComposerDrop.MaxImageBytes + 1]);
		Assert.Throws<IOException>(() => ComposerDrop.RawImage(data));
	}

	public void Dispose() => Directory.Delete(_root, recursive: true);
}
