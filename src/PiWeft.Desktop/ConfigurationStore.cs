using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiWeft.Desktop;

public enum ConfigFile { Models, Auth, Settings, Mcp, Usage }

/// <summary>A bounded file snapshot. Invalid JSON remains available for explicit source repair.</summary>
public sealed record ConfigurationDocument(ConfigFile File, bool Project, string Path, string Raw, string Revision, string? Error)
{
	public JsonObject Parse() => Error is null
		? ConfigurationStore.ParseObject(Raw.Length == 0 ? "{}" : Raw)
		: throw new ArgumentException(Error);
}

public sealed class ConfigurationConflictException() : IOException(Strings.ConfigConflict);

/// <summary>Configuration writes use revision checks and same-directory atomic replacement.</summary>
public sealed class ConfigurationStore(string agentDir, string cwd)
{
	public const int MaxBytes = 2 * 1024 * 1024;
	private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
	public static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	public ConfigurationDocument Read(ConfigFile file, bool project = false)
	{
		if (project && file == ConfigFile.Auth) throw new ArgumentException(Strings.ConfigAuthGlobalOnly);
		var name = file switch { ConfigFile.Models => "models.json", ConfigFile.Auth => "auth.json", ConfigFile.Settings => "settings.json", ConfigFile.Usage => "usage-queries.json", _ => "mcp.json" };
		var path = System.IO.Path.Combine(project ? System.IO.Path.Combine(System.IO.Path.GetFullPath(cwd), ".pi") : System.IO.Path.GetFullPath(agentDir), name);
		var (raw, revision) = ReadRaw(path);
		string? error = null;
		if (revision != "missing")
		{
			try { Validate(file, ParseObject(raw)); }
			catch (ArgumentException ex) { error = ex.Message; }
		}
		return new(file, project, path, raw, revision, error);
	}

	public ConfigurationDocument Save(ConfigurationDocument document, string raw)
	{
		var expectedPath = Read(document.File, document.Project).Path;
		if (!string.Equals(expectedPath, document.Path, StringComparison.Ordinal)) throw new ArgumentException(Strings.ConfigInvalidPath);
		if (Encoding.UTF8.GetByteCount(raw) > MaxBytes) throw new ArgumentException(Strings.ConfigTooLarge);
		var root = ParseObject(raw);
		Validate(document.File, root);
		var formatted = root.ToJsonString(WriteOptions) + "\n";
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(document.Path)!);

		// Serialize desktop writers and respect pi's proper-lockfile marker. Recheck revision immediately
		// before rename too; never claim that non-cooperating external editors share this lock.
		var lockPath = document.Path + ".desktop-write-lock";
		using var fileLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		var temporary = document.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			CheckCurrent();
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
			if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			using (var stream = new FileStream(temporary, options))
			{
				stream.Write(Encoding.UTF8.GetBytes(formatted));
				stream.Flush(flushToDisk: true);
			}
			CheckCurrent();
			System.IO.File.Move(temporary, document.Path, overwrite: true);
		}
		finally
		{
			if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
			fileLock.Dispose();
			System.IO.File.Delete(lockPath);
		}
		return Read(document.File, document.Project);

		void CheckCurrent()
		{
			if (Directory.Exists(document.Path + ".lock") || System.IO.File.Exists(document.Path + ".lock") ||
				ReadRaw(document.Path).Revision != document.Revision) throw new ConfigurationConflictException();
		}
	}

	public static JsonObject ParseObject(string raw)
	{
		try
		{
			return JsonNode.Parse(raw.TrimStart('\uFEFF'), documentOptions: ReadOptions) as JsonObject
				?? throw new ArgumentException(Strings.ConfigObjectRequired);
		}
		catch (JsonException) { throw new ArgumentException(Strings.ConfigInvalidJson); }
	}

	private static (string Raw, string Revision) ReadRaw(string path)
	{
		if (!System.IO.File.Exists(path)) return (string.Empty, "missing");
		using var stream = System.IO.File.OpenRead(path);
		if (stream.Length > MaxBytes) throw new IOException(Strings.ConfigTooLarge);
		using var buffer = new MemoryStream();
		var bytes = new byte[8192];
		int count;
		while ((count = stream.Read(bytes)) > 0)
		{
			if (buffer.Length + count > MaxBytes) throw new IOException(Strings.ConfigTooLarge);
			buffer.Write(bytes, 0, count);
		}
		var content = buffer.ToArray();
		return (Encoding.UTF8.GetString(content), Convert.ToHexString(SHA256.HashData(content)));
	}

	private static void Validate(ConfigFile file, JsonObject root)
	{
		if (file == ConfigFile.Models)
		{
			if (root.ContainsKey("providers") && root["providers"] is not JsonObject) throw new ArgumentException(Strings.ConfigProvidersRequired);
			foreach (var (name, node) in root["providers"] as JsonObject ?? [])
			{
				ConfigurationModels.ValidateId(name);
				if (node is not JsonObject provider) throw new ArgumentException(Strings.ConfigProvidersRequired);
				StringFields(provider, "baseUrl", "api", "apiKey");
				StringMap(provider, "headers");
				if (provider.ContainsKey("models") && provider["models"] is not JsonArray) throw new ArgumentException(Strings.ConfigModelsRequired);
				var ids = new HashSet<string>(StringComparer.Ordinal);
				foreach (var model in provider["models"] as JsonArray ?? [])
				{
					if (model is not JsonObject obj || obj["id"] is not JsonValue id || !id.TryGetValue<string>(out var text) ||
						string.IsNullOrWhiteSpace(text) || !ids.Add(text)) throw new ArgumentException(Strings.ConfigModelIdRequired);
					StringFields(obj, "name", "api", "baseUrl");
					BoolFields(obj, "reasoning");
					StringArray(obj, "input");
					StringMap(obj, "headers");
					foreach (var key in new[] { "contextWindow", "maxTokens" })
					{
						if (obj.ContainsKey(key) && (obj[key] is not JsonValue number || !number.TryGetValue<int>(out var value) || value <= 0))
							throw new ArgumentException(Strings.ConfigPositiveNumber);
					}
				}
			}
		}
		if (file == ConfigFile.Settings)
		{
			StringFields(root, "defaultProvider", "defaultModel", "defaultThinkingLevel");
			BoolFields(root, "enableSkillCommands");
			foreach (var section in new[] { "compaction", "retry" })
			{
				if (!root.ContainsKey(section)) continue;
				if (root[section] is not JsonObject settings) throw new ArgumentException(Strings.ConfigInvalidValue(section));
				BoolFields(settings, "enabled");
			}
		}
		if (file == ConfigFile.Auth)
		{
			foreach (var (name, entry) in root)
			{
				if (entry is not JsonObject credential) throw new ArgumentException(Strings.ConfigInvalidValue(name));
				StringFields(credential, "type", "key");
			}
		}
		if (file == ConfigFile.Mcp)
		{
			if (root.ContainsKey("mcpServers") && root["mcpServers"] is not JsonObject) throw new ArgumentException(Strings.ConfigMcpRequired);
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (var (name, entry) in root["mcpServers"] as JsonObject ?? [])
			{
				if (!PiWeft.Mcp.McpConfig.IsValidName(name) || !names.Add(PiWeft.Mcp.McpConfig.CanonicalName(name))) throw new ArgumentException(Strings.ConfigMcpName);
				if (entry is not JsonObject server) throw new ArgumentException(Strings.ConfigMcpRequired);
				StringFields(server, "command", "url", "cwd", "description");
				BoolFields(server, "enabled");
				StringArray(server, "args");
				StringMap(server, "env"); StringMap(server, "headers");
			}
		}
	}

	private static void StringFields(JsonObject obj, params string[] keys)
	{
		foreach (var key in keys)
			if (obj.ContainsKey(key) && (obj[key] is not JsonValue value || !value.TryGetValue<string>(out _)))
				throw new ArgumentException(Strings.ConfigInvalidValue(key));
	}

	private static void BoolFields(JsonObject obj, params string[] keys)
	{
		foreach (var key in keys)
			if (obj.ContainsKey(key) && (obj[key] is not JsonValue value || !value.TryGetValue<bool>(out _)))
				throw new ArgumentException(Strings.ConfigInvalidValue(key));
	}

	private static void StringArray(JsonObject obj, string key)
	{
		if (obj.ContainsKey(key) && (obj[key] is not JsonArray array || array.Any(node => node is not JsonValue value || !value.TryGetValue<string>(out _))))
			throw new ArgumentException(Strings.ConfigInvalidValue(key));
	}

	private static void StringMap(JsonObject obj, string key)
	{
		if (obj.ContainsKey(key) && (obj[key] is not JsonObject map || map.Any(pair => pair.Value is not JsonValue value || !value.TryGetValue<string>(out _))))
			throw new ArgumentException(Strings.ConfigInvalidValue(key));
	}
}
