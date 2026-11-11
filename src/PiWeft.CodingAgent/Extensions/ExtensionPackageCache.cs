using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using PiWeft.CodingAgent.Resources;
using PiWeft.CodingAgent.Session;

namespace PiWeft.CodingAgent.Extensions;

/// <c>-e npm:&lt;pkg&gt;</c> / <c>-e git:&lt;repo&gt;@&lt;ref&gt;</c> 的本地物化目录。
/// 与 <c>settings.packages</c> 分开存放：这两类来源只属于本次命令行，不进设置文件，
/// 也不该被 <c>pi update --extensions</c> 当成已安装的包。
public static class ExtensionPackageCache
{
	private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

	// 发现会随每个新会话跑一遍；不缓存的话 -e npm:<pkg> 每开一个标签就重装一次。
	// /reload 通过 Invalidate 让下一次发现重新检查远端。
	private static readonly ConcurrentDictionary<string, (string? Root, string? Error)> Resolved =
		new(StringComparer.Ordinal);

	/// 清掉本次进程内的解析结果，让下一次发现重新检查（<c>/reload</c> 用）。
	public static void Invalidate() => Resolved.Clear();

	public static bool IsPackageSpec(string value) =>
		value.StartsWith("npm:", StringComparison.Ordinal) ||
		value.StartsWith("git:", StringComparison.Ordinal);

	/// 返回包根目录；<paramref name="error"/> 非空表示无法物化（离线且无缓存、命令失败等）。
	/// 已有缓存时不会因为网络失败而丢弃它：扩展还能用旧版本。
	public static string? Resolve(string spec, string? agentDir, out string? error)
	{
		error = null;
		var resolvedAgentDir = agentDir ?? SessionPaths.GetAgentDir();
		var key = resolvedAgentDir + "\0" + spec;
		if (Resolved.TryGetValue(key, out var cached))
		{
			error = cached.Error;
			return cached.Root;
		}

		var root = Path.Combine(resolvedAgentDir, "extension-packages");
		string? resolved;
		try
		{
			resolved = spec.StartsWith("npm:", StringComparison.Ordinal)
				? ResolveNpm(spec[4..], root, out error)
				: ResolveGit(spec, root, out error);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			error = exception.Message;
			resolved = null;
		}

		Resolved[key] = (resolved, error);
		return resolved;
	}

	private static string? ResolveNpm(string npmSpec, string root, out string? error)
	{
		error = null;
		var name = PiPackageStore.NpmPackageName(npmSpec);
		if (name is null)
		{
			error = $"无法解析 npm 包名：{npmSpec}";
			return null;
		}

		var prefix = Path.Combine(root, "npm");
		var target = Path.Combine(prefix, "node_modules", name);
		var hasExactVersion = HasExactVersion(npmSpec);
		if (Directory.Exists(target) && (hasExactVersion || PiEnvironment.Offline))
		{
			return target;
		}

		if (PiEnvironment.Offline)
		{
			error = "离线模式下无法安装 -e npm 包";
			return null;
		}

		Directory.CreateDirectory(prefix);
		var arguments = new List<string> { "install", "--prefix", prefix, npmSpec, "--legacy-peer-deps", "--no-audit", "--no-fund", "--loglevel", "error" };
		var (exitCode, output, stderr) = Run(NpmExecutable(), arguments, prefix);
		if (exitCode == 0 && Directory.Exists(target))
		{
			return target;
		}

		// 安装失败但旧缓存还在：继续用旧版本，只把原因交给诊断。
		if (Directory.Exists(target))
		{
			error = $"npm 安装失败（退出码 {exitCode}），继续使用缓存版本";
			return target;
		}

		error = $"npm 安装失败（退出码 {exitCode}）：{Tail(stderr.Length > 0 ? stderr : output)}";
		return null;
	}

	private static string? ResolveGit(string spec, string root, out string? error)
	{
		error = null;
		var value = spec[4..];
		var (url, reference) = SplitReference(value);
		if (url is null)
		{
			error = $"无法解析 git 来源：{spec}";
			return null;
		}

		var slug = PiPackageStore.GitSlug(spec);
		if (slug is null)
		{
			error = $"无法解析 git 目录名：{spec}";
			return null;
		}

		var parent = Path.Combine(root, "git");
		var target = Path.Combine(parent, slug);
		if (!Directory.Exists(target))
		{
			if (PiEnvironment.Offline)
			{
				error = "离线模式下无法安装 -e git 包";
				return null;
			}

			Directory.CreateDirectory(parent);
			var clone = new List<string> { "clone", "--depth", "1" };
			if (reference is { Length: > 0 })
			{
				clone.Add("--branch");
				clone.Add(reference);
			}

			clone.Add(url);
			clone.Add(target);
			var (exitCode, output, stderr) = Run("git", clone, parent);
			if (exitCode == 0 && Directory.Exists(target))
			{
				return target;
			}

			TryDelete(target);
			error = $"git clone 失败（退出码 {exitCode}）：{Tail(stderr.Length > 0 ? stderr : output)}";
			return null;
		}

		if (PiEnvironment.Offline)
		{
			return target;
		}

		// 已检出同一个 commit 时不必再拉取；分支或未指定 ref 则跟着远端走。
		if (reference is { Length: > 0 } && reference.Length >= 7 && IsCommit(target, reference))
		{
			return target;
		}

		var fetch = reference is { Length: > 0 }
			? new List<string> { "fetch", "--depth", "1", "origin", reference }
			: ["pull", "--ff-only"];
		var (fetchCode, fetchOut, fetchError) = Run("git", fetch, target);
		if (fetchCode != 0)
		{
			error = $"git 更新失败（退出码 {fetchCode}），继续使用缓存版本：{Tail(fetchError.Length > 0 ? fetchError : fetchOut)}";
			return target;
		}

		if (reference is { Length: > 0 })
		{
			var reset = Run("git", ["reset", "--hard", "FETCH_HEAD"], target);
			if (reset.ExitCode != 0)
			{
				error = $"git reset 失败（退出码 {reset.ExitCode}），继续使用缓存版本：{Tail(reset.Error)}";
			}
		}

		return target;
	}

	private static bool HasExactVersion(string spec)
	{
		var separator = spec.StartsWith('@') ? spec.IndexOf('@', 1) : spec.IndexOf('@');
		if (separator <= 0)
		{
			return false;
		}

		var version = spec[(separator + 1)..];
		return version.Length > 0 && version[0] is >= '0' and <= '9';
	}

	private static (string? Url, string? Reference) SplitReference(string value)
	{
		var text = value;
		string? reference = null;
		var at = text.LastIndexOf('@');
		// SCP 形式（git@host:path）里的 @ 在路径之前，不是 ref。
		if (at > 0 && text[..at].Contains('/'))
		{
			reference = text[(at + 1)..];
			text = text[..at];
		}

		if (text.Length == 0)
		{
			return (null, null);
		}

		var url = text.StartsWith("git@", StringComparison.Ordinal) ||
			text.StartsWith("http://", StringComparison.Ordinal) ||
			text.StartsWith("https://", StringComparison.Ordinal)
			? text
			: "https://" + text;
		return (url, reference is { Length: > 0 } ? reference : null);
	}

	private static bool IsCommit(string repository, string reference)
	{
		var (exitCode, output, _) = Run("git", ["rev-parse", "HEAD"], repository);
		return exitCode == 0 && output.Trim().StartsWith(reference, StringComparison.OrdinalIgnoreCase);
	}

	private static string NpmExecutable()
	{
		const string executable = "npm";
		return OperatingSystem.IsWindows() ? executable + ".cmd" : executable;
	}

	private static (int ExitCode, string Output, string Error) Run(
		string fileName,
		IReadOnlyList<string> arguments,
		string workingDirectory)
	{
		var startInfo = new ProcessStartInfo(fileName)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		// 非交互：npm/git 在等密码或确认时应当失败，而不是把扩展加载卡死。
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		startInfo.Environment["npm_config_yes"] = "true";
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return (-1, string.Empty, $"无法启动 {fileName}");
			}

			var stdout = process.StandardOutput.ReadToEndAsync();
			var stderr = process.StandardError.ReadToEndAsync();
			if (!process.WaitForExit(InstallTimeout))
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// 进程已退出或无权杀：结果按超时处理。
				}

				return (-1, string.Empty, $"{fileName} 超时（{InstallTimeout.TotalMinutes:0} 分钟）");
			}

			return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
		}
		catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
		{
			return (-1, string.Empty, exception.Message);
		}
	}

	private static string Tail(string text)
	{
		var trimmed = text.Trim();
		return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
	}

	private static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// 清理失败不影响错误上报。
		}
	}
}
