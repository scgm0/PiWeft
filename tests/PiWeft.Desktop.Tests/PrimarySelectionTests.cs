using System.Diagnostics;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class PrimarySelectionTests : IDisposable
{
	private readonly IPrimarySelection? _previous = PrimarySelectionText.Service;

	[Fact]
	public void SelectingRawTextPublishesToPrimaryWithoutChangingClipboard()
	{
		var primary = new FakePrimary();
		PrimarySelectionText.Service = primary;
		var text = MessageText.Create("first 中文 second");
		text.Select(6, 2);
		Assert.Equal("中文", primary.Published.Last());
		text.Select(0, 5);
		Assert.Equal("first", primary.Published.Last());
		var password = new PasswordBox().Password("do not publish").PrimarySelection();
		var count = primary.Published.Count;
		password.SelectAll();
		Assert.Equal(count, primary.Published.Count);
	}

	[Fact]
	public async Task PasteAtCaretKeepsDraftAndCanBeUndone()
	{
		var primary = new FakePrimary { Result = "中文\nnext" };
		PrimarySelectionText.Service = primary;
		var composer = new MultiLineTextBox().Text("ab").PrimarySelection();
		composer.Select(1, 0);
		await PrimarySelectionText.PasteAsync(composer);
		Assert.Equal("a中文\nnextb", composer.Text);
		composer.Undo();
		Assert.Equal("ab", composer.Text);
	}

	[Fact]
	public async Task SingleLinePasteNormalizesNewlinesAndReadOnlyTextDoesNotChange()
	{
		PrimarySelectionText.Service = new FakePrimary { Result = "first\nsecond" };
		var single = new TextBox().PrimarySelection();
		await PrimarySelectionText.PasteAsync(single);
		Assert.Equal("first second", single.Text);
		var readOnly = MessageText.Create("original");
		await PrimarySelectionText.PasteAsync(readOnly);
		Assert.Equal("original", readOnly.Text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DelayedPasteDoesNotOverwriteTypingOrHiddenTab(bool hide)
	{
		var pending = new TaskCompletionSource<string?>();
		PrimarySelectionText.Service = new FakePrimary { Pending = pending.Task };
		var composer = new MultiLineTextBox().PrimarySelection();
		var parent = new Border().Child(composer);
		var paste = PrimarySelectionText.PasteAsync(composer);
		if (hide) parent.IsVisible(false); else composer.Text = "new draft";
		pending.SetResult("stale");
		await paste;
		Assert.Equal(hide ? "" : "new draft", composer.Text);
	}

	[Fact]
	public async Task LinuxSelectionInteroperatesWithXclipAndDoesNotTouchClipboard()
	{
		if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !File.Exists("/usr/bin/xclip")) return;
		using var service = LinuxPrimarySelection.Create();
		Assert.NotNull(service);
		await SetClipboard("clipboard independent");
		service.Publish("中文 PRIMARY\nsecond line");
		await service.PendingPublish;
		string? received = null;
		for (var attempt = 0; attempt < 20; attempt++)
		{
			received = await service.ReadAsync();
			if (received == "中文 PRIMARY\nsecond line") break;
			await Task.Delay(25);
		}
		Assert.Equal("中文 PRIMARY\nsecond line", received);
		Assert.Equal("clipboard independent", await ReadClipboard());
		service.Publish("replacement");
		await service.PendingPublish;
		await Task.Delay(50);
		Assert.Equal("replacement", await service.ReadAsync());
		service.Publish("");
		Assert.Equal("replacement", await service.ReadAsync());
		service.Dispose();
		Assert.Null(await service.ReadAsync());
	}

	[Fact]
	public async Task FailedOrOversizedSelectionReadsFailClosed()
	{
		if (!OperatingSystem.IsLinux()) return;
		using var missing = new LinuxPrimarySelection(new("/no/such/primary-helper", [], []));
		Assert.Null(await missing.ReadAsync());
		missing.Publish("test");
		await missing.PendingPublish;
		using var oversized = new LinuxPrimarySelection(new("/usr/bin/head", [], ["-c", "1048577", "/dev/zero"]));
		Assert.Null(await oversized.ReadAsync());
	}

	private static async Task SetClipboard(string text)
	{
		using var process = StartXclip("-selection", "clipboard", "-in");
		await process.StandardInput.WriteAsync(text);
		process.StandardInput.Close();
		await process.WaitForExitAsync();
	}

	private static async Task<string> ReadClipboard()
	{
		using var process = StartXclip("-selection", "clipboard", "-out");
		var text = await process.StandardOutput.ReadToEndAsync();
		await process.WaitForExitAsync();
		return text;
	}

	private static Process StartXclip(params string[] arguments)
	{
		var process = new Process { StartInfo = new("/usr/bin/xclip")
		{
			UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
		} };
		foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
		process.Start();
		return process;
	}

	public void Dispose() => PrimarySelectionText.Service = _previous;

	private sealed class FakePrimary : IPrimarySelection
	{
		public List<string> Published { get; } = [];
		public string? Result { get; init; }
		public Task<string?>? Pending { get; init; }
		public void Publish(string text) => Published.Add(text);
		public Task<string?> ReadAsync() => Pending ?? Task.FromResult(Result);
	}
}
