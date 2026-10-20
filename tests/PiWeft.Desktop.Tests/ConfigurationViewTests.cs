using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

[Collection("mewui-controls")]
public sealed class ConfigurationViewTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "pi-config-view-" + Guid.NewGuid().ToString("N"));
	private string AgentDir => Path.Combine(_root, "agent");
	private string Cwd => Path.Combine(_root, "project");
	private ConfigurationStore Store => new(AgentDir, Cwd);

	[Fact]
	public void ProviderAndModelFormsSaveUsableConfiguration()
	{
		var changes = new List<ConfigFile>();
		var view = new ConfigurationView(AgentDir, Cwd, changes.Add);
		view.EditProvider(null);
		var fields = Find<TextBox>(view.View).ToArray();
		fields[0].Text = "local";
		fields[1].Text = "http://localhost:8080/v1";
		Find<PasswordBox>(view.View).Single().Password = "test-key";
		Assert.True(view.IsDirty);
		view.ApplyTheme();
		Assert.Same(fields[0], Find<TextBox>(view.View).First());
		view.SaveDraft();
		Assert.False(view.IsDirty);
		view.EditModel("local", null);
		fields = Find<TextBox>(view.View).ToArray();
		fields[0].Text = "model";
		fields[1].Text = "Local Model";
		fields[2].Text = "32768";
		fields[3].Text = "4096";
		view.SaveDraft();
		var root = Store.Read(ConfigFile.Models).Parse();
		Assert.Equal("test-key", root["providers"]!["local"]!["apiKey"]!.GetValue<string>());
		Assert.Equal("model", root["providers"]!["local"]!["models"]![0]!["id"]!.GetValue<string>());
		Assert.Equal(new[] { ConfigFile.Models, ConfigFile.Models }, changes);
	}

	[Fact]
	public async Task SourceDraftSurvivesThemeRefreshAndWritesOnlyAfterSave()
	{
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Settings, source: true);
		Assert.Empty(Find<MultiLineTextBox>(view.View));
		Assert.False(view.CanSave);
		Find<CheckBox>(view.View).Single().IsChecked = true;
		var editor = Find<MultiLineTextBox>(view.View).Single();
		Assert.True(view.CanSave);
		editor.Text = "{\"unknown\":42}";
		view.ApplyTheme();
		Assert.Same(editor, Find<MultiLineTextBox>(view.View).Single());
		Assert.True(view.IsDirty);
		Assert.False(File.Exists(Store.Read(ConfigFile.Settings).Path));
		view.SaveDraft();
		Assert.False(view.IsDirty);
		Assert.Equal(42, Store.Read(ConfigFile.Settings).Parse()["unknown"]!.GetValue<int>());
	}

	[Fact]
	public async Task SavingProjectSettingsDoesNotTurnInheritedOptionsIntoOverrides()
	{
		Store.Save(Store.Read(ConfigFile.Settings), "{\"compaction\":{\"enabled\":false},\"retry\":{\"enabled\":false}}");
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Settings);
		Find<ComboBox>(view.View).First().SelectedIndex = 1;
		Assert.All(Find<CheckBox>(view.View), check => Assert.False(check.IsChecked));
		Find<TextBox>(view.View).First().Text = "local";
		view.SaveDraft();
		var saved = Store.Read(ConfigFile.Settings, project: true).Parse();
		Assert.Equal("local", saved["defaultProvider"]!.GetValue<string>());
		Assert.False(saved.ContainsKey("compaction"));
		Assert.False(saved.ContainsKey("retry"));
		Assert.False(saved.ContainsKey("defaultThinkingLevel"));
	}

	[Fact]
	public async Task AgentToolsAndCodemodeFormRoundTripsThroughSettingsJson()
	{
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Settings);
		ConversationPerformanceTests.Layout(view.View, 640, 720);

		// 默认工具：「全部禁用」写成显式空数组（pi 语义），而不是退回默认集。
		ClickText(view.View, Strings.ConfigToolsDisableAll);
		Assert.True(view.IsDirty);
		view.SaveDraft();
		Assert.Empty(Store.Read(ConfigFile.Settings).Parse()["defaultTools"]!.AsArray());

		// Codemode 模式：继承 → only。
		var combos = Find<ComboBox>(view.View).ToArray();
		Assert.Equal(3, combos.Length);
		combos[2].SelectedIndex = 2;
		view.SaveDraft();
		Assert.Equal("only", Store.Read(ConfigFile.Settings).Parse()["codemode"]!["mode"]!.GetValue<string>());

		// 声明预算：0 是有效值（只列命名空间）。
		Find<TextBox>(view.View).ToArray()[2].Text = "0";
		view.SaveDraft();
		Assert.Equal(0, Store.Read(ConfigFile.Settings).Parse()["codemode"]!["inlineBudget"]!.GetValue<int>());

		// 回到继承：模式选「继承」、预算留空，codemode 键整体移除。
		Find<ComboBox>(view.View).ToArray()[2].SelectedIndex = 0;
		Find<TextBox>(view.View).ToArray()[2].Text = string.Empty;
		view.SaveDraft();
		Assert.False(Store.Read(ConfigFile.Settings).Parse().ContainsKey("codemode"));
	}

	[Fact]
	public async Task ProjectToolsFormInheritsGlobalAndWritesOnlyTheDelta()
	{
		Store.Save(Store.Read(ConfigFile.Settings), """{"defaultTools":["read","bash"]}""");
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Settings);
		Find<ComboBox>(view.View).First().SelectedIndex = 1;
		ConversationPerformanceTests.Layout(view.View, 640, 720);

		// chips 反映继承到的全局集合；项目层本身不写 defaultTools。
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == "read");
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == "bash");
		Assert.Contains(Find<TextBlock>(view.View), block => block.Text == Strings.ConfigInheritGlobal);

		// 用 chip 的 × 关掉 bash：项目层只落增量，全局文件不动。
		ImagePreviewTests.Click(Find<Button>(view.View)
			.First(button => HoverHints.Text(button) == Strings.ConfigRemoveTool("bash")));
		view.SaveDraft();
		Assert.Equal(new[] { "-bash" }, Store.Read(ConfigFile.Settings, project: true).Parse()["defaultTools"]!
			.AsArray().Select(node => node!.GetValue<string>()));
		Assert.Equal(new[] { "read", "bash" }, Store.Read(ConfigFile.Settings).Parse()["defaultTools"]!
			.AsArray().Select(node => node!.GetValue<string>()));
	}

	[Fact]
	public void SaveAvailabilityTracksTheDraftAndRaisesStateChanges()
	{
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		Assert.True(view.CanSave);
		var states = 0;
		view.StateChanged += () => states++;
		view.EditProvider(null);
		Find<TextBox>(view.View).First().Text = "local";
		Assert.True(view.IsDirty);
		Assert.True(states > 0);
		view.Discard();
		Assert.False(view.IsDirty);
		Assert.True(view.CanSave);
	}

	[Fact]
	public async Task AuthHeaderSaveCommitsTheCredentialFormAndClearsSensitiveInput()
	{
		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Auth);
		Find<TextBox>(view.View).Single().Text = "local";
		var password = Find<PasswordBox>(view.View).Single();
		password.Password = "test-key";
		view.SaveDraft();
		Assert.False(view.IsDirty);
		Assert.Empty(password.Password);
		Assert.Equal("test-key", Store.Read(ConfigFile.Auth).Parse()["local"]!["key"]!.GetValue<string>());
	}

	/// <summary>按可见文案点击表单里的文字按钮（图标按钮通过 HoverHints 文案定位）。</summary>
	private static void ClickText(UIElement root, string text) =>
		ImagePreviewTests.Click(Find<Button>(root).First(button => Find<TextBlock>(button).Any(block => block.Text == text)));

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
