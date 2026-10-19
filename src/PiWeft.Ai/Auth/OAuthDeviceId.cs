using System.Text.RegularExpressions;

namespace PiWeft.Ai.Auth;

/// <summary>为需要安装级 device ID 的 OAuth 流程提供稳定 UUID。</summary>
public static partial class OAuthDeviceId
{
	public static string GetOrCreate(string? agentDir = null)
	{
		var path = Path.Combine(agentDir ?? AgentPaths.AgentDirectory(), "device-id");
		try
		{
			if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing && Uuid().IsMatch(existing))
			{
				return existing.ToLowerInvariant();
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 读取失败时生成新 ID 并尝试写入。
		}

		var id = Guid.NewGuid().ToString("D");
		try
		{
			var directory = Path.GetDirectoryName(path);
			if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);
			File.WriteAllText(path, id);
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 无持久化权限时仍返回本次会话的 ID。
		}

		return id;
	}

	[GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
	private static partial Regex Uuid();
}
