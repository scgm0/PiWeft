using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Mcp;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

internal sealed partial class ConfigurationView
{
	private void BuildMcp()
	{
		_saveForm = () => Save(_root);
		var servers = _root["mcpServers"] as JsonObject ?? new JsonObject();
		_body.Children(new DockPanel().LastChildFill().Children(
			AppIcons.PrimaryButton("plus", Strings.ConfigAddMcp, () => EditMcp(null)).DockRight(),
			new TextBlock().Text(Strings.ConfigMcpHint).FontSize(AppTheme.FontSizeCaption).MutedText().TextWrapping(TextWrapping.Wrap)));
		if (servers.Count == 0) _body.Children(FormCard(Strings.ConfigMcpEmpty, Strings.ConfigMcpHint,
			AppIcons.TextButton("plus", Strings.ConfigAddMcp, () => EditMcp(null)).HorizontalAlignment(HorizontalAlignment.Left)));
		foreach (var (name, node) in servers)
		{
			if (node is not JsonObject server) continue;
			var enabled = Check(Strings.ConfigEnabled, !server.ContainsKey("enabled") || ConfigurationModels.Flag(server, "enabled"));
			enabled.OnCheckedChanged(_ => { server["enabled"] = enabled.IsChecked == true; SetDirty(true); });
			_body.Children(new Border().StyleName(AppStyles.Card).Padding(new Thickness(AppTheme.Space3)).Child(
				new DockPanel().LastChildFill().Children(
					AppIcons.Button("trash", Strings.Delete, () => _ = DeleteAsync(name, () => { servers.Remove(name); SetDirty(true); Build(); })).DockRight(),
					AppIcons.Button("pencil", Strings.ConfigEditMcp, () => EditMcp(name)).DockRight(), enabled.DockRight(),
					new StackPanel().Spacing(AppTheme.Space1).Children(
						Label(name),
						AuthRow(name, server)))));
		}
	}

	/// <summary>
	/// 授权状态与入口。只对 HTTP 服务器显示 —— stdio 服务器没有 OAuth，放一个「授权」按钮
	/// 在那里只会让人以为点它能解决连不上的问题。
	/// <para>
	/// 这里读的是**已保存的**配置（磁盘上那份），不是编辑中的草稿：授权要拿真实 URL 去发现
	/// 授权服务器并换令牌，用没保存的草稿换回来的令牌跟服务器对不上。
	/// </para>
	/// </summary>
	private Element AuthRow(string name, JsonObject server)
	{
		if (ConfigurationModels.Text(server, "url") is not { Length: > 0 } url)
		{
			return new TextBlock();
		}

		var config = new McpServerConfig { Name = name, Url = url };
		var status = StatusText(config);
		var button = AppIcons.TextButton(
			McpLogin.SignedIn(config, _store.AgentDir) ? "refresh-cw" : "key-round",
			status == Strings.McpAuthorized ? Strings.McpReauthorize : Strings.McpAuthorize,
			() => _ = AuthorizeAsync(name, url));

		// 只有已有凭据时才给「清除凭据」：没登录过时放一个禁用按钮只是噪声。
		var logout = AppIcons.TextButton("trash", Strings.McpSignout, () =>
		{
			if (McpLogin.Logout(config, _store.AgentDir)) Toasts.Success(View, Strings.McpSignedOut(name));
			Build();
		});
		logout.IsVisible(McpLogin.SignedIn(config, _store.AgentDir));

		return new DockPanel().LastChildFill().Children(
			new StackPanel().Orientation(Orientation.Horizontal).Spacing(AppTheme.Space1).Children(button, logout).DockRight(),
			// 已授权用最弱档：状态是背景信息，不该和服务器名抢注意力。
			AuthStatusText(status, McpLogin.SignedIn(config, _store.AgentDir)));
	}

	private static TextBlock AuthStatusText(string status, bool signedIn)
	{
		var block = new TextBlock().Text(status).FontSize(AppTheme.FontSizeMicro)
			.VerticalAlignment(VerticalAlignment.Center);
		return signedIn ? block.SubtleText() : block.FaintText();
	}

	private string StatusText(McpServerConfig config) =>
		!McpLogin.SignedIn(config, _store.AgentDir) ? Strings.McpNotAuthorized
			: McpLogin.IsExpired(config, _store.AgentDir) ? Strings.McpTokenExpired
				: Strings.McpAuthorized;

	/// <summary>
	/// 走 <see cref="McpLogin"/> 的完整流程（探测挑战 → 发现元数据 → 客户端凭据或浏览器授权码）。
	/// 与 CLI 共用同一实现，所以两边支持哪些授权方式不会分叉。
	/// </summary>
	private async Task AuthorizeAsync(string name, string url)
	{
		var config = new McpServerConfig { Name = name, Url = url };
		Toasts.Show(View, Strings.McpAuthorizing(name));
		var outcome = await McpLogin.RunAsync(
			new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
			config,
			_store.AgentDir,
			// 授权链接交给系统浏览器；打不开时提示用户手动访问（CLI 走的是同一条回调）。
			url => { if (!Shell.Open(url)) Toasts.Error(View, Strings.McpBrowserFailed(url)); },
			CancellationToken.None).ConfigureAwait(true);

		if (outcome.Ok) Toasts.Success(View, Strings.McpAuthorizedDone(name));
		else Toasts.Error(View, $"{name}: {outcome.Message}");

		Build();
	}

	private void EditMcp(string? oldName)
	{
		var server = oldName is not null ? _root["mcpServers"]?[oldName] : null;
		var name = Input(oldName ?? "");
		var transport = new ComboBox().ItemsSource(ItemsView.Create(new[] { "stdio", "HTTP" })).Width(180)
			.SelectedIndex(ConfigurationModels.Text(server, "url").Length > 0 ? 1 : 0);
		var command = Input(ConfigurationModels.Text(server, "command"));
		var url = Input(ConfigurationModels.Text(server, "url"));
		var args = Input(server?["args"]?.ToJsonString() ?? "[]");
		var enabled = Check(Strings.ConfigEnabled, server is null || !server.AsObject().ContainsKey("enabled") || ConfigurationModels.Flag(server, "enabled"));
		transport.SelectionChanged += _ => SetDirty(true);
		_body.Clear();
		_body.Children(Label(oldName is null ? Strings.ConfigAddMcp : Strings.ConfigEditMcp), Field(Strings.ConfigName, name),
			Field(Strings.ConfigTransport, transport), Field(Strings.ConfigCommand, command), Field(Strings.ConfigArguments, args),
			Field(Strings.ConfigBaseUrl, url), enabled, ActionButton(Strings.Cancel, () => _ = CancelFormAsync()));
		_saveForm = () =>
		{
			var id = name.Text.Trim();
			if (!McpConfig.IsValidName(id)) throw new ArgumentException(Strings.ConfigMcpName);
			var next = _root.DeepClone().AsObject();
			var servers = next["mcpServers"] as JsonObject ?? new JsonObject();
			if (next["mcpServers"] is null) next["mcpServers"] = servers;
			if (servers.Any(pair => pair.Key != oldName && McpConfig.CanonicalName(pair.Key) == McpConfig.CanonicalName(id)))
				throw new ArgumentException(Strings.ConfigNameExists);
			var entry = server is JsonObject existing ? existing.DeepClone().AsObject() : new JsonObject();
			if (transport.SelectedIndex == 0)
			{
				if (string.IsNullOrWhiteSpace(command.Text)) throw new ArgumentException(Strings.ConfigCommandRequired);
				JsonArray arguments;
				try { arguments = JsonNode.Parse(args.Text) as JsonArray ?? throw new ArgumentException(Strings.ConfigArgumentsRequired); }
				catch (JsonException) { throw new ArgumentException(Strings.ConfigArgumentsRequired); }
				if (arguments.Any(value => value is not JsonValue v || !v.TryGetValue<string>(out _))) throw new ArgumentException(Strings.ConfigArgumentsRequired);
				entry["command"] = command.Text.Trim(); entry["args"] = arguments; entry.Remove("url");
			}
			else
			{
				if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0)
					throw new ArgumentException(Strings.ConfigUrlRequired);
				entry["url"] = url.Text.Trim(); entry.Remove("command"); entry.Remove("args");
			}
			entry["enabled"] = enabled.IsChecked == true;
			if (oldName is not null && oldName != id) servers.Remove(oldName);
			servers[id] = entry;
			Save(next);
		};
	}
}
