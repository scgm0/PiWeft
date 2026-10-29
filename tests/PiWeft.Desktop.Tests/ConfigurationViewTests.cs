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

		// 声明预算：0 是有效值（只列命名空间）。运行卡新增了重试次数/基础延迟两个输入，
		// TextBox 顺序是 provider、model、retryMax、retryDelay、codemode 预算。
		Find<TextBox>(view.View).ToArray()[4].Text = "0";
		view.SaveDraft();
		Assert.Equal(0, Store.Read(ConfigFile.Settings).Parse()["codemode"]!["inlineBudget"]!.GetValue<int>());

		// 回到继承：模式选「继承」、预算留空，codemode 键整体移除。
		Find<ComboBox>(view.View).ToArray()[2].SelectedIndex = 0;
		Find<TextBox>(view.View).ToArray()[4].Text = string.Empty;
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

	/// <summary>
	/// MCP 授权入口的渲染规则：HTTP 服务器才有授权行（stdio 没有 OAuth，放个按钮只会误导人），
	/// 状态文案要跟磁盘上的凭据一致，「清除凭据」只在真有待清除的东西时出现。
	/// </summary>
	/// <summary>按钮标签按内容文本匹配：AppIcons.TextButton 把文字放在内容里，HoverTip 只有图标按钮才有。</summary>
	private static List<Button> ButtonsLabeled(ConfigurationView view, string label) =>
		[.. Find<Button>(view.View).Where(button =>
			button.Content is StackPanel panel && Find<TextBlock>(panel).Any(block => block.Text == label))];

	private static int CountButtonsLabeled(ConfigurationView view, string label) => ButtonsLabeled(view, label).Count;

	[Fact]
	public async Task McpRowShowsAuthorizationStatusOnlyForHttpServers()
	{
		Directory.CreateDirectory(AgentDir);
		File.WriteAllText(Path.Combine(AgentDir, "mcp.json"),
			"""{"mcpServers":{"local":{"command":"srv","args":[],"enabled":true},"remote":{"url":"https://mcp.example/mcp","enabled":true}}}""");

		var view = new ConfigurationView(AgentDir, Cwd, _ => { });
		await view.OpenAsync(ConfigFile.Mcp);

		var texts = Find<TextBlock>(view.View).Select(static block => block.Text).ToHashSet(StringComparer.Ordinal);
		Assert.Contains(Strings.McpNotAuthorized, texts);
		Assert.Contains(Strings.McpAuthorize, texts);
		// 只有一个服务器需要授权（另一个是 stdio），所以授权按钮也只该有一个。
		// TextButton 的标签在内容里、不在 HoverTip 里，按内容找。
		Assert.Equal(1, CountButtonsLabeled(view, Strings.McpAuthorize));
		// 没凭据可清除时「清除凭据」保持不可见：留一个能点的空按钮只会让人点了没反应。
		Assert.False(ButtonsLabeled(view, Strings.McpSignout).Single().IsVisible);

		// 授权按钮必须真的能占到位：之前出现过它在树里、却因为被塞进一个零宽容器而在界面上看不见。
		var authorize = ButtonsLabeled(view, Strings.McpAuthorize).Single();
		Element? row = authorize.Parent;
		while (row is not DockPanel)
		{
			row = row?.Parent;
		}

		Assert.NotNull(row);
		row!.Measure(new Size(620, 60));
		row.Arrange(new Rect(0, 0, 620, 60));
		Assert.True(authorize.Bounds.Width > 24, $"授权按钮宽度 {authorize.Bounds.Width}：看不见等同于没有这个入口");
		Assert.InRange(authorize.Bounds.Right, 0, 621);

		// 存好凭据后状态与按钮文案都要跟上（界面上「未授权」会让人反复去点登录）。
		PiWeft.Mcp.McpOAuthStore.Save("https://mcp.example/mcp", new PiWeft.Mcp.McpOAuthToken
		{
			Access = "live",
			ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
		}, AgentDir);

		view.Discard(); // 重新读盘：磁盘上的凭据变了，状态行不能停留在旧值
		texts = Find<TextBlock>(view.View).Select(static block => block.Text).ToHashSet(StringComparer.Ordinal);
		Assert.Contains(Strings.McpAuthorized, texts);
		Assert.Contains(Strings.McpReauthorize, texts);
		Assert.True(ButtonsLabeled(view, Strings.McpSignout).Single().IsVisible);

		// 令牌过期要报「已过期」而不是笼统的「已授权」：两者的处理动作不同。
		PiWeft.Mcp.McpOAuthStore.Save("https://mcp.example/mcp", new PiWeft.Mcp.McpOAuthToken
		{
			Access = "stale",
			ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
		}, AgentDir);
		view.Discard(); // 重新读盘：磁盘上的凭据变了，状态行不能停留在旧值
		Assert.Contains(Strings.McpTokenExpired, Find<TextBlock>(view.View).Select(static block => block.Text));
	}
}
