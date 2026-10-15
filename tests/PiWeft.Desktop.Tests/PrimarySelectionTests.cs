using System.Diagnostics;
using System.Text;
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
	public async Task NativeSelectionOwnerServesOtherX11Clients()
	{
		if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
		using var owner = X11PrimarySelection.Create();
		using var reader = X11PrimarySelection.Create();
		Assert.NotNull(owner);
		Assert.NotNull(reader);

		// 两个独立连接：owner 持有选区，reader 必须经由 SelectionRequest/SelectionNotify 协议取回文本。
		owner!.Publish("原生 PRIMARY\n中文 second line");
		await owner.PendingPublish;
		Assert.Equal("原生 PRIMARY\n中文 second line", await ReadEventually(reader!, "原生 PRIMARY\n中文 second line"));

		if (!File.Exists("/usr/bin/xclip")) return;
		owner.Publish("由外部 xclip 读取");
		await owner.PendingPublish;
		Assert.Equal("由外部 xclip 读取", await ReadEventually(new XclipPrimary(), "由外部 xclip 读取"));

		// 反向：外部程序持有 PRIMARY 时，原生实现必须经 ConvertSelection 读回。
		await SetPrimaryWithXclip("xclip owned primary");
		Assert.Equal("xclip owned primary", await ReadEventually(reader!, "xclip owned primary"));
	}

	[Fact]
	public async Task IncrementalTransferCarriesTextLargerThanOneRequest()
	{
		if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
		using var owner = X11PrimarySelection.Create();
		using var reader = X11PrimarySelection.Create();
		Assert.NotNull(owner);
		Assert.NotNull(reader);

		// 明显超过单块阈值：必须经由 ICCCM INCR 分块往返才能取回。
		var expected = string.Concat(Enumerable.Repeat("中文增量传输-", 200_000));
		Assert.True(Encoding.UTF8.GetByteCount(expected) > owner!.TransferChunkBytes,
			"fixture must exceed one request chunk so the INCR path is exercised");
		owner.Publish(expected);
		await owner.PendingPublish;
		var received = await ReadEventually(reader!, expected);
		Assert.True(received == expected, $"incremental owner mismatch, length={(received?.Length).GetValueOrDefault()}");

		if (!File.Exists("/usr/bin/xclip")) return;
		// 反向：外部 owner 交出大文本时，接收端同样要经 INCR 累积后再解码。
		var external = string.Concat(Enumerable.Repeat("xclip大文本-", 150_000));
		await SetPrimaryWithXclip(external);
		var fromXclip = await ReadEventually(reader!, external);
		Assert.True(fromXclip == external, $"incremental receive mismatch, length={(fromXclip?.Length).GetValueOrDefault()}");
	}

	private static async Task<string?> ReadEventually(IPrimarySelection selection, string expected)
	{
		string? text = null;
		for (var attempt = 0; attempt < 40; attempt++)
		{
			text = await selection.ReadAsync();
			if (text == expected) break;
			await Task.Delay(25);
		}
		return text;
	}

	private static async Task SetPrimaryWithXclip(string text)
	{
		using var process = StartXclip("-selection", "primary", "-in");
		await process.StandardInput.WriteAsync(text);
		process.StandardInput.Close();
		await process.WaitForExitAsync();
	}

	private sealed class XclipPrimary : IPrimarySelection
	{
		public Task PendingPublish => Task.CompletedTask;
		public void Publish(string text) { }
		public async Task<string?> ReadAsync()
		{
			using var process = StartXclip("-selection", "primary", "-out");
			var text = await process.StandardOutput.ReadToEndAsync();
			await process.WaitForExitAsync();
			return process.ExitCode == 0 ? text : null;
		}
	}

	[Fact]
	public async Task FailedOrOversizedSelectionReadsFailClosed()
	{
		if (!OperatingSystem.IsLinux()) return;
		using var missing = new LinuxPrimarySelection(new("/no/such/primary-helper", [], []));
		Assert.Null(await missing.ReadAsync());
		missing.Publish("test");
		await missing.PendingPublish;
		using var oversized = new LinuxPrimarySelection(new("/usr/bin/head", [],
			["-c", (LinuxPrimarySelection.MaxBytes + 1).ToString(), "/dev/zero"]));
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
		public Task PendingPublish => Task.CompletedTask;
		public void Publish(string text) => Published.Add(text);
		public Task<string?> ReadAsync() => Pending ?? Task.FromResult(Result);
	}
}
