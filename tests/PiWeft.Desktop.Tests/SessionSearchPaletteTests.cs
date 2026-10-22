using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class SessionSearchPaletteTests
{
	private static readonly IReadOnlyList<SessionListItem> Catalog =
	[
		Item("chat", "/work/alpha"),
		Item("json-i-18n", "/work/godot"),
		Item("notes", "/work/godot"),
	];

	private static SessionListItem Item(string name, string cwd) => new()
	{
		Path = Path.Combine(cwd, name + ".jsonl"),
		Id = name,
		Name = name,
		Cwd = cwd,
	};

	[Fact]
	public void OpeningAttachesToTheWindowAndClosingDetachesOnce()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		var palette = new SessionSearchPalette(() => Catalog, static _ => { });
		var closed = 0;
		palette.Closed += () => closed++;

		palette.Show(owner);
		Assert.True(palette.IsOpen);
		Assert.Same(window, palette.View.Parent);
		Assert.Equal(1, window.OverlayLayer.Count);

		palette.Hide();
		Assert.False(palette.IsOpen);
		Assert.Null(palette.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
		Assert.Equal(1, closed);
		palette.Hide();
		Assert.Equal(1, closed);
	}

	[Fact]
	public void MissingCatalogSaysScanningInsteadOfNoSessions()
	{
		// 侧栏摘要按需加载：列表还没到时打开搜索，不能显示「暂无会话」。
		var owner = new Border();
		var window = new Window().Content(owner);
		var loading = true;
		IReadOnlyList<SessionListItem> items = [];
		var palette = new SessionSearchPalette(() => items, static _ => { }, () => loading);
		palette.Show(owner);

		Assert.Contains(Find<TextBlock>(palette.View), block => block.Text == Strings.SessionsScanning);

		// 扫描完成：宿主把数据挂上并让弹窗按当前关键字重跑，提示随之消失。
		items = Catalog;
		loading = false;
		palette.Reload();
		// 有结果时状态行收起（文本可能留着上一次的值，但不再可见）。
		Assert.All(
			Find<TextBlock>(palette.View).Where(block => block.Text == Strings.SessionsScanning),
			block => Assert.False(block.IsVisible));
		Assert.Equal(Catalog.Count, palette.Matches.Count);
		palette.Hide();
	}

	[Fact]
	public void OpeningThePaletteClosesTheCommandPaletteLikeTheOtherOverlays()
	{
		var window = new Window();
		var commands = new CommandPalette(() => []);
		var search = new SessionSearchPalette(() => Catalog, static _ => { });
		commands.Show(window);
		search.Show(window);
		Assert.False(commands.IsOpen);
		Assert.True(search.IsOpen);
		Assert.Equal(1, window.OverlayLayer.Count);
		search.Hide();
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void TypingFiltersByNameAndEnterOpensTheSelectedSession()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		SessionListItem? opened = null;
		var palette = new SessionSearchPalette(() => Catalog, item => opened = item);
		palette.Show(owner);

		var query = Find<TextBox>(palette.View).First();
		query.Text = "i-18n";
		Assert.Equal(["json-i-18n"], palette.Matches.Select(static item => item.Name));
		var titles = Find<TextBlock>(palette.View).Select(static text => text.Text)
			.Where(static text => text.Length > 0).ToArray();
		Assert.Equal(["Esc", Strings.SessionSearch, "json-i-18n", "/work/godot"], titles);

		Press(query, Key.Enter);
		Assert.Same(Catalog[1], opened);
		Assert.False(palette.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void ArrowKeysMoveTheSelectionAndEscapeCloses()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		var palette = new SessionSearchPalette(() => Catalog, static _ => { });
		palette.Show(owner);
		var query = Find<TextBox>(palette.View).First();
		var rows = Find<Button>(palette.View).Where(button => HoverHints.Text(button) is not null).ToArray();
		Assert.Equal(3, rows.Length);
		Assert.Equal(AppStyles.SelectedButton, rows[0].StyleName);

		Press(query, Key.Down);
		Assert.Equal(AppStyles.GhostButton, rows[0].StyleName);
		Assert.Equal(AppStyles.SelectedButton, rows[1].StyleName);

		Press(query, Key.Escape);
		Assert.False(palette.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void NoMatchesShowsTheHintAndTheBlankAreaClosesThePalette()
	{
		var owner = new Border();
		var window = new Window().Content(owner);
		var palette = new SessionSearchPalette(() => Catalog, static _ => { });
		palette.Show(owner);
		var query = Find<TextBox>(palette.View).First();
		query.Text = "no-such-session";
		var hint = Find<TextBlock>(palette.View).Single(text => text.Text == Strings.NoMatches);
		Assert.True(hint.IsVisible);

		var backdrop = Find<Button>(palette.View).First(button => HoverHints.Text(button) is null);
		ImagePreviewTests.Click(backdrop);
		Assert.False(palette.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void HugeCatalogsRenderABoundedListAndSaySo()
	{
		var many = Enumerable.Range(0, 1200)
			.Select(index => Item($"session-{index:D4}", "/work/big"))
			.ToArray();
		var owner = new Border();
		var window = new Window().Content(owner);
		var palette = new SessionSearchPalette(() => many, static _ => { });
		palette.Show(owner);

		// 有界列表：不因为会话多就构造上千个带 hover 提示的行。
		Assert.Equal(200, palette.Matches.Count);
		Assert.Contains(Find<TextBlock>(palette.View), text => text.IsVisible && text.Text.Contains("1200", StringComparison.Ordinal));

		// 键盘选择仍在本段内移动，不会越界到未渲染的行。
		var query = Find<TextBox>(palette.View).First();
		for (var step = 0; step < 5; step++) Press(query, Key.Down);
		Press(query, Key.Enter);
		palette.Hide();
	}

	[Fact]
	public void DetachedOwnersDoNotOpenThePalette()
	{
		var palette = new SessionSearchPalette(() => Catalog, static _ => { });
		palette.Show(new Border());
		Assert.False(palette.IsOpen);
		Assert.Null(palette.View.Parent);
	}

	private static void Press(UIElement element, Key key) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [new KeyEventArgs(key, 0)]);
}
