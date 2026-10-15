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
					Label(name))));
		}
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
