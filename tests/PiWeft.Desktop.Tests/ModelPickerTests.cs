using System.Reflection;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Ai;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ModelPickerTests
{
	static ModelPickerTests() => typeof(Program).GetMethod("RegisterPlatform", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);

	private static Model Model(string id, string provider = "fixture") =>
		new() { Id = id, Name = "Display " + id, Provider = provider, Api = "openai-completions" };

	[Fact]
	public void SearchCoversAllModelsAndPickingKeepsTheProviderIdentity()
	{
		var models = Enumerable.Range(0, 40).Select(index => Model($"model-{index:D2}")).ToArray();
		var other = Model("model-39", "other");
		Model? selected = null;
		var picker = new ModelPicker(() => [.. models, other], () => models[0], model => selected = model);
		var window = new Window();
		picker.Show(window);
		var query = Find<TextBox>(picker.View).Single();
		query.Text = "other model-39";
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == Strings.ModelResultCount(1, 41));
		Key(query, Aprillz.MewUI.Key.Enter);
		Assert.Same(other, selected);
		Assert.False(picker.IsOpen);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void RefreshRechecksAvailabilityAndDoesNotForceTheCurrentModelIntoAnEmptyList()
	{
		IReadOnlyList<Model> models = [Model("current")];
		var picker = new ModelPicker(() => models, () => Model("current"), _ => throw new InvalidOperationException());
		var window = new Window();
		picker.Show(window);
		models = [];
		ImagePreviewTests.Click(Find<Button>(picker.View).Single(button => HoverHints.Text(button) == Strings.RefreshModels));
		Assert.Contains(Find<TextBlock>(picker.View), block => block.IsVisible && block.Text == Strings.ModelNoneAvailable);
		Key(Find<TextBox>(picker.View).Single(), Aprillz.MewUI.Key.Enter);
		Assert.True(picker.IsOpen);
		picker.Hide();
	}

	[Fact]
	public void ModelPickerClosesWithEscape()
	{
		// 面板互斥替换由 PaletteOverlayTests.OpeningAnotherPaletteClosesThePreviousWindowOverlay 覆盖；
		// 这里只保留 Escape 关闭模型面板自身的语义。
		var model = Model("current");
		var picker = new ModelPicker(() => [model], () => model, _ => { });
		var window = new Window();
		picker.Show(window);
		Key(picker.View, Aprillz.MewUI.Key.Escape);
		Assert.False(picker.IsOpen);
		Assert.Null(picker.View.Parent);
		Assert.Equal(0, window.OverlayLayer.Count);
	}

	[Fact]
	public void GroupedRowsAreVirtualizedAndTheRealizedModelButtonPicksItsBoundModel()
	{
		var models = Enumerable.Range(0, 1000).Select(index => Model($"model-{index:D4}")).ToArray();
		Model? selected = null;
		var picker = new ModelPicker(() => models, () => models[0], model => selected = model);
		var window = new Window();
		picker.Show(window);
		picker.View.Measure(new Size(900, 700));
		picker.View.Arrange(new Rect(0, 0, 900, 700));
		var rows = Find<Button>(picker.View).Where(button => button.Tag is TreeViewNode { Tag: PiWeft.Ai.Model }).ToArray();
		Assert.NotEmpty(rows);
		Assert.True(rows.Length < 30, $"Realized {rows.Length} rows for a 360 px viewport");
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == "fixture · 1000 " + Strings.ModelsCountUnit);
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == "✓ fixture/model-0000");
		var chosen = Assert.IsType<Model>(((TreeViewNode)rows[0].Tag!).Tag);
		ImagePreviewTests.Click(rows[0]);
		Assert.Same(chosen, selected);
		Assert.False(picker.IsOpen);
	}

	[Fact]
	public void SearchingAfterScrollingStartsAtTheFirstResult()
	{
		var models = Enumerable.Range(0, 1000).Select(index => Model($"model-{index:D4}")).ToArray();
		var picker = new ModelPicker(() => models, () => models[0], _ => { });
		var window = new Window();
		picker.Show(window);
		picker.View.Measure(new Size(900, 700));
		picker.View.Arrange(new Rect(0, 0, 900, 700));
		var tree = Find<TreeView>(picker.View).Single();
		var scroll = Find<ScrollViewer>(tree).First();
		scroll.SetScrollOffsets(0, 900);
		picker.View.Measure(new Size(900, 700));
		picker.View.Arrange(new Rect(0, 0, 900, 700));
		Assert.True(scroll.VerticalOffset > 0);

		Find<TextBox>(picker.View).Single().Text = "model-0999";
		picker.View.Measure(new Size(900, 700));
		picker.View.Arrange(new Rect(0, 0, 900, 700));
		Assert.Equal(0, scroll.VerticalOffset, 1);
		picker.Hide();
	}

	[Fact]
	public void ModelPanelFitsNarrowWindows()
	{
		var model = Model("current");
		var picker = new ModelPicker(() => [model], () => model, _ => { });
		var window = new Window();
		picker.Show(window);
		picker.View.Measure(new Size(420, 700));
		picker.View.Arrange(new Rect(0, 0, 420, 700));
		var query = Find<TextBox>(picker.View).Single();
		Assert.True(query.ActualWidth > 200);
		Assert.True(query.ActualWidth < 420);
		picker.Hide();
	}

	[Fact]
	public void OpeningAndSearchingUseOneCatalogSnapshotUntilExplicitRefresh()
	{
		var calls = 0;
		var model = Model("current");
		var picker = new ModelPicker(() => { calls++; return [model, model]; }, () => model, _ => { });
		var window = new Window();
		picker.Show(window);
		var query = Find<TextBox>(picker.View).Single();
		query.Text = "Display";
		query.Text = "fixture";
		picker.Show(window);
		Assert.Equal(1, calls);
		Assert.Contains(Find<TextBlock>(picker.View), block => block.Text == Strings.ModelResultCount(1, 1));
		picker.Hide();
		picker.Show(window);
		Assert.Equal(2, calls);
		picker.Hide();
	}

	private static void Key(UIElement element, Aprillz.MewUI.Key key) =>
		typeof(UIElement).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(element, [new KeyEventArgs(key, 0)]);
}
