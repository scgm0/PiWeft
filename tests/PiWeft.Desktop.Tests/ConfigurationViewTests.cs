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
		Find<CheckBox>(view.View).Single().IsChecked = true;
		var editor = Find<MultiLineTextBox>(view.View).Single();
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

	public void Dispose()
	{
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
