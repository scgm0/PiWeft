using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Migrations;

public sealed record MigrationReport(IReadOnlyList<string> MigratedAuthProviders, IReadOnlyList<string> Warnings)
{
	public static MigrationReport Empty { get; } = new([], []);
}

/// 启动期的一次性迁移，镜像 pi 的 <c>migrations.ts</c>：
/// <list type="bullet">
/// <item><description>凭据：<c>oauth.json</c> 与 <c>settings.json.apiKeys</c> 合并成 <c>auth.json</c>
/// （pi 0.30 起唯一的凭据文件），否则老用户显示「未登录」。</description></item>
/// <item><description>会话落点：根目录遗留的 <c>*.jsonl</c> 搬进 <c>sessions/&lt;编码 cwd&gt;/</c>，否则侧栏看不到。</description></item>
/// <item><description>资源目录：<c>commands/</c> 改名 <c>prompts/</c>。</description></item>
/// </list>
/// 每一步失败关闭：单个文件读不懂只记警告，不阻塞启动，也不删来源文件。迁移幂等，每次启动都可跑。
public static class AgentMigrations
{
	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// 执行全部启动期迁移。<paramref name="agentDir"/> 为空时用 <c>PI_CODING_AGENT_DIR</c> 或默认目录。
	/// <paramref name="projectTrustOverride"/> 是本进程的显式信任开关（<c>-a</c>/<c>-na</c>）；
	/// 项目级的 <c>commands/</c> 迁移只在受信时执行，不在别人的仓库里做写操作。
	public static MigrationReport Run(string cwd, string? agentDir = null, bool? projectTrustOverride = null)
	{
		var directory = agentDir ?? SessionPaths.GetAgentDir();
		var warnings = new List<string>();

		// agent 目录还不存在（首次运行）时，凭据与会话迁移无事可做；
		// 但项目里的 commands/ 仍然该迁移，不能因为用户目录缺失就整体跳过。
		var providers = new List<string>();
		if (Directory.Exists(directory))
		{
			providers.AddRange(MigrateAuth(directory, warnings));
			MoveSessionsFromAgentRoot(directory, warnings);
		}

		// 用户级目录没有信任问题；项目目录只在已受信时动，避免在别人仓库里做写操作。
		MoveCommandsToPrompts(directory, warnings);
		// 先看有没有要迁移的目录，再问信任：绝大多数项目没有 commands/，
		// 不该为一次 CLI 启动多读 trust.json 与 settings.json。
		var project = Path.Combine(Path.GetFullPath(cwd), SessionPaths.ConfigDirName);
		if (Directory.Exists(Path.Combine(project, "commands")) &&
			IsTrusted(cwd, directory, projectTrustOverride))
		{
			MoveCommandsToPrompts(project, warnings);
		}

		return new MigrationReport(providers, warnings);
	}

	private static bool IsTrusted(string cwd, string agentDir, bool? trustOverride)
	{
		try
		{
			return ProjectTrust.Resolve(
				cwd,
				trustOverride: trustOverride,
				CodingAgentSettings.GlobalDefaultProjectTrust(agentDir),
				agentDir);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	/// 把旧凭据并入 <c>auth.json</c>。<c>auth.json</c> 已存在时整体跳过（与 pi 一致：
	/// 新文件是权威，旧的 <c>oauth.json</c> 留着由用户自己清理）。
	private static List<string> MigrateAuth(string agentDir, List<string> warnings)
	{
		var providers = new List<string>();
		var authPath = Path.Combine(agentDir, "auth.json");
		if (File.Exists(authPath))
		{
			return providers;
		}

		var migrated = new JsonObject();
		var oauthPath = Path.Combine(agentDir, "oauth.json");
		var oauthRead = true;
		if (File.Exists(oauthPath))
		{
			if (ReadObject(oauthPath, warnings, "oauth.json") is { } oauth)
			{
				foreach (var (provider, node) in oauth)
				{
					if (node is not JsonObject credential)
					{
						continue;
					}

					var entry = (JsonObject)credential.DeepClone();
					if (entry["type"] is null)
					{
						entry["type"] = "oauth";
					}

					migrated[provider] = entry;
					providers.Add(provider);
				}
			}
			else
			{
				oauthRead = false;
			}
		}

		// settings.apiKeys 是 pi 更早的存储位置。删除动作推迟到 auth.json 写成功之后：
		// 磁盘满或权限不足时，先把凭据从设置里删掉再写失败，等于把密钥抹了。
		var settingsPath = Path.Combine(agentDir, "settings.json");
		JsonObject? settingsToClean = null;
		if (File.Exists(settingsPath) && ReadObject(settingsPath, warnings, "settings.json") is { } settings)
		{
			if (settings["apiKeys"] is JsonObject apiKeys)
			{
				foreach (var (provider, node) in apiKeys)
				{
					if (migrated.ContainsKey(provider))
					{
						continue;
					}

					if (node is not JsonValue value || !value.TryGetValue<string>(out var key) || key.Length == 0)
					{
						continue;
					}

					migrated[provider] = new JsonObject
					{
						["type"] = "api_key",
						["key"] = key,
					};
					providers.Add(provider);
				}

				settingsToClean = settings;
			}
		}

		if (migrated.Count > 0)
		{
			// 先写 auth.json，再清理旧位置：任何一步失败时凭据都还在某处，下次启动可以重试。
			WriteJson(authPath, migrated, privateFile: true);

			if (settingsToClean is not null)
			{
				settingsToClean.Remove("apiKeys");
				WriteJson(settingsPath, settingsToClean, privateFile: false);
			}

			if (oauthRead && File.Exists(oauthPath))
			{
				try
				{
					File.Move(oauthPath, oauthPath + ".migrated", overwrite: true);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					warnings.Add($"oauth.json 已并入 auth.json，但改名失败：{exception.Message}");
				}
			}
		}

		return providers;
	}

	private static void MoveSessionsFromAgentRoot(string agentDir, List<string> warnings)
	{
		string[] files;
		try
		{
			files = Directory.GetFiles(agentDir, "*" + SessionFormat.Extension, SearchOption.TopDirectoryOnly);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"扫描 agent 目录失败：{exception.Message}");
			return;
		}

		foreach (var path in files)
		{
			try
			{
				if (ReadFirstLine(path) is not { Length: > 0 } line ||
					JsonNode.Parse(line) is not JsonObject header ||
					header["type"]?.GetValue<string>() != "session" ||
					header["cwd"]?.GetValue<string>() is not { Length: > 0 } sessionCwd)
				{
					continue;
				}

				var targetDirectory = Path.Combine(agentDir, "sessions", EncodeCwd(sessionCwd));
				Directory.CreateDirectory(targetDirectory);
				var target = Path.Combine(targetDirectory, Path.GetFileName(path));
				if (File.Exists(target))
				{
					// 同名文件已存在：保留现状，两份都还能在侧栏里分别打开。
					continue;
				}

				File.Move(path, target);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
				or ArgumentException or NotSupportedException or InvalidOperationException)
			{
				warnings.Add($"会话 {Path.GetFileName(path)} 迁移失败：{exception.Message}");
			}
		}
	}

	internal static string EncodeCwd(string cwd)
	{
		var trimmed = cwd.TrimStart('/', '\\');
		var encoded = trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
		return $"--{encoded}--";
	}

	private static void MoveCommandsToPrompts(string directory, List<string> warnings)
	{
		var commands = Path.Combine(directory, "commands");
		var prompts = Path.Combine(directory, "prompts");
		if (!Directory.Exists(commands) || Directory.Exists(prompts))
		{
			return;
		}

		try
		{
			Directory.Move(commands, prompts);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			warnings.Add($"commands/ → prompts/ 迁移失败（{directory}）：{exception.Message}");
		}
	}

	private static string? ReadFirstLine(string path)
	{
		using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadLine();
	}

	private static JsonObject? ReadObject(string path, List<string> warnings, string label)
	{
		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			warnings.Add($"{label} 无法解析，跳过迁移：{exception.Message}");
			return null;
		}
	}

	private static void WriteJson(string path, JsonObject value, bool privateFile)
	{
		var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllText(temporary, value.ToJsonString(WriteOptions));
			if (privateFile && !OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
			else if (!OperatingSystem.IsWindows() && File.Exists(path))
			{
				File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
			}

			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// 清理失败不影响主流程。
			}
		}
	}
}
