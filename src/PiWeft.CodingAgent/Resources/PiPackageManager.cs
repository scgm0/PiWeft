using System.Diagnostics;
using System.Text;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Settings;

namespace PiWeft.CodingAgent.Resources;

public sealed record PackageResult
{
	public required bool Ok { get; init; }

	public required string Message { get; init; }

	public string? Detail { get; init; }

	public static PackageResult Success(string message, string? detail = null) => new() { Ok = true, Message = message, Detail = detail };

	public static PackageResult Failure(string message, string? detail = null) => new() { Ok = false, Message = message, Detail = detail };
}

public interface IPackageCommandRunner
{
	Task<(int ExitCode, string Output, string Error)> RunAsync(
		string fileName,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		CancellationToken cancellationToken);
}

public sealed class ProcessPackageCommandRunner : IPackageCommandRunner
{
	private const int MaxOutputChars = 32 * 1024;

	public static ProcessPackageCommandRunner Instance { get; } = new();

	public async Task<(int ExitCode, string Output, string Error)> RunAsync(
		string fileName,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		CancellationToken cancellationToken)
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

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo);
		if (process is null)
		{
			return (-1, string.Empty, $"无法启动命令：{fileName}");
		}

		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
		try
		{
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		return (process.ExitCode, Truncate(await stdout.ConfigureAwait(false)), Truncate(await stderr.ConfigureAwait(false)));
	}

	private static void TryKill(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
		{
			// 进程已退出或平台不支持：忽略。
		}
	}

	private static string Truncate(string text) =>
		text.Length <= MaxOutputChars ? text : text[^MaxOutputChars..];
}

public sealed record PackageOptions
{
	public required string AgentDir { get; init; }

	public required string Cwd { get; init; }

	public bool Local { get; init; }

	public IReadOnlyList<string> NpmCommand { get; init; } = ["npm"];

	public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

	public ResourceScope Scope => Local ? ResourceScope.Project : ResourceScope.User;

	public string ScopeRoot => Local
		? Path.Combine(Path.GetFullPath(Cwd), SessionPaths.ConfigDirName)
		: Path.GetFullPath(AgentDir);

	public string SettingsPath => Local
		? CodingAgentSettings.ProjectPath(Cwd)
		: CodingAgentSettings.GlobalPath(AgentDir);

	public PackageSource Source(string value) => new() { Value = value, Scope = Scope, ScopeRoot = ScopeRoot };
}

/// pi 包的安装、移除与更新。与 pi 一致：npm 装到 <c>&lt;scopeRoot&gt;/npm/node_modules/&lt;name&gt;</c>，
/// git 克隆到 <c>&lt;scopeRoot&gt;/git/&lt;slug&gt;</c>，本地路径只登记不复制；来源写进 settings 的 <c>packages</c>。
public static class PiPackageManager
{
	public static async Task<PackageResult> InstallAsync(
		string source,
		PackageOptions options,
		IPackageCommandRunner? runner = null,
		CancellationToken cancellationToken = default)
	{
		var trimmed = source.Trim();
		if (trimmed.Length == 0)
		{
			return PackageResult.Failure("包来源不能为空");
		}

		var spec = options.Source(trimmed);
		var materialized = await MaterializeAsync(spec, options, runner ?? ProcessPackageCommandRunner.Instance, cancellationToken)
			.ConfigureAwait(false);
		if (!materialized.Ok)
		{
			return materialized;
		}

		try
		{
			CodingAgentSettings.AddPackage(options.SettingsPath, trimmed);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return PackageResult.Failure($"无法更新 {options.SettingsPath}", exception.Message);
		}

		return PackageResult.Success($"已安装 {trimmed}", materialized.Detail);
	}

	public static async Task<PackageResult> RemoveAsync(
		string source,
		PackageOptions options,
		IPackageCommandRunner? runner = null,
		CancellationToken cancellationToken = default)
	{
		var trimmed = source.Trim();
		if (trimmed.Length == 0)
		{
			return PackageResult.Failure("包来源不能为空");
		}

		var spec = options.Source(trimmed);
		var removed = false;
		try
		{
			removed = CodingAgentSettings.RemovePackage(options.SettingsPath, trimmed);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return PackageResult.Failure($"无法更新 {options.SettingsPath}", exception.Message);
		}

		if (!removed)
		{
			return PackageResult.Failure($"{options.SettingsPath} 里没有声明 {trimmed}");
		}

		// 本地包只取消登记，不删除用户自己的目录。
		var isLocal = !IsRemote(trimmed);
		if (!isLocal && PiPackageStore.ResolveRoot(spec) is { } root && Directory.Exists(root))
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return PackageResult.Failure($"已取消登记，但删除 {root} 失败", exception.Message);
			}
		}

		return PackageResult.Success($"已移除 {trimmed}");
	}

	public static async Task<IReadOnlyList<PackageResult>> UpdateAsync(
		string? source,
		PackageOptions options,
		IPackageCommandRunner? runner = null,
		CancellationToken cancellationToken = default)
	{
		var commandRunner = runner ?? ProcessPackageCommandRunner.Instance;
		IReadOnlyList<PackageSource> sources = source is { Length: > 0 }
			? [options.Source(source.Trim())]
			: [.. PiPackageStore.DeclaredSources(options.Cwd, options.AgentDir, includeProject: options.Local)
				.Where(candidate => candidate.Scope == options.Scope)];

		var results = new List<PackageResult>(sources.Count);
		foreach (var spec in sources)
		{
			results.Add(await UpdateOneAsync(spec, options, commandRunner, cancellationToken).ConfigureAwait(false));
		}

		return results;
	}

	private static async Task<PackageResult> UpdateOneAsync(
		PackageSource spec,
		PackageOptions options,
		IPackageCommandRunner runner,
		CancellationToken cancellationToken)
	{
		if (spec.Value.StartsWith("npm:", StringComparison.Ordinal))
		{
			return await InstallNpmAsync(spec.Value[4..], spec, options, runner, cancellationToken).ConfigureAwait(false);
		}

		if (IsRemote(spec.Value))
		{
			var root = PiPackageStore.ResolveRoot(spec);
			if (root is null || !Directory.Exists(root))
			{
				return PackageResult.Failure($"{spec.Value} 尚未安装");
			}

			var (_, reference) = SplitGitReference(spec.Value);
			IReadOnlyList<string> arguments = reference is { Length: > 0 }
				? ["fetch", "--depth", "1", "origin", reference]
				: ["pull", "--ff-only"];
			var fetch = await ExecuteAsync("git", arguments, root, options.Timeout, runner, cancellationToken).ConfigureAwait(false);
			if (fetch.ExitCode != 0)
			{
				return PackageResult.Failure($"git 更新失败（退出码 {fetch.ExitCode}）", fetch.Error);
			}

			if (reference is { Length: > 0 })
			{
				var checkout = await ExecuteAsync("git", ["checkout", "--detach", "FETCH_HEAD"], root, options.Timeout, runner, cancellationToken)
					.ConfigureAwait(false);
				if (checkout.ExitCode != 0)
				{
					return PackageResult.Failure($"git checkout 失败（退出码 {checkout.ExitCode}）", checkout.Error);
				}
			}

			return PackageResult.Success($"已更新 {spec.Value}", root);
		}

		return PackageResult.Success($"本地包无需更新：{spec.Value}");
	}

	private static async Task<PackageResult> MaterializeAsync(
		PackageSource spec,
		PackageOptions options,
		IPackageCommandRunner runner,
		CancellationToken cancellationToken)
	{
		if (spec.Value.StartsWith("npm:", StringComparison.Ordinal))
		{
			return await InstallNpmAsync(spec.Value[4..], spec, options, runner, cancellationToken).ConfigureAwait(false);
		}

		if (IsRemote(spec.Value))
		{
			return await InstallGitAsync(spec, options, runner, cancellationToken).ConfigureAwait(false);
		}

		var root = PiPackageStore.ResolveRoot(spec);
		if (root is null || !Directory.Exists(root))
		{
			return PackageResult.Failure($"本地包目录不存在：{root ?? spec.Value}");
		}

		return File.Exists(Path.Combine(root, ResourcePaths.PackageManifestName))
			? PackageResult.Success($"已登记本地包 {spec.Value}", root)
			: PackageResult.Failure($"本地包缺少 {ResourcePaths.PackageManifestName}：{root}");
	}

	private static async Task<PackageResult> InstallNpmAsync(
		string npmSpec,
		PackageSource spec,
		PackageOptions options,
		IPackageCommandRunner runner,
		CancellationToken cancellationToken)
	{
		var name = PiPackageStore.NpmPackageName(npmSpec);
		if (name is null)
		{
			return PackageResult.Failure($"无法解析 npm 包名：{npmSpec}");
		}

		var prefix = Path.Combine(spec.ScopeRoot, ResourcePaths.NpmDirectoryName);
		Directory.CreateDirectory(prefix);

		// settings.npmCommand 的第一项是可执行文件，其余是固定参数（例如 pnpm 的 --silent），
		// 必须原样保留在 install 之前，否则用户的 npm 配置只生效一半。
		var arguments = new List<string>();
		for (var index = 1; index < options.NpmCommand.Count; index++)
		{
			arguments.Add(options.NpmCommand[index]);
		}

		arguments.AddRange(["install", "--prefix", prefix, npmSpec, "--legacy-peer-deps", "--no-audit", "--no-fund", "--loglevel", "error"]);
		var execution = await ExecuteAsync(NpmExecutable(options.NpmCommand), arguments, prefix, options.Timeout, runner, cancellationToken)
			.ConfigureAwait(false);
		if (execution.ExitCode != 0)
		{
			return PackageResult.Failure($"npm install 失败（退出码 {execution.ExitCode}）", execution.Error);
		}

		var root = Path.Combine(prefix, "node_modules", name);
		return Directory.Exists(root)
			? PackageResult.Success($"已安装 {spec.Value}", root)
			: PackageResult.Failure($"npm 未生成 {root}", execution.Output);
	}

	private static async Task<PackageResult> InstallGitAsync(
		PackageSource spec,
		PackageOptions options,
		IPackageCommandRunner runner,
		CancellationToken cancellationToken)
	{
		var (url, reference) = SplitGitReference(spec.Value);
		if (url is null)
		{
			return PackageResult.Failure($"无法解析 git 来源：{spec.Value}");
		}

		var slug = PiPackageStore.GitSlug(spec.Value);
		if (slug is null)
		{
			return PackageResult.Failure($"无法解析 git 目录名：{spec.Value}");
		}

		var parent = Path.Combine(spec.ScopeRoot, "git");
		Directory.CreateDirectory(parent);
		var target = Path.Combine(parent, slug);
		if (Directory.Exists(target))
		{
			return PackageResult.Failure($"目标目录已存在，先移除或更新：{target}");
		}

		var arguments = new List<string> { "clone", "--depth", "1" };
		if (reference is { Length: > 0 })
		{
			arguments.Add("--branch");
			arguments.Add(reference);
		}

		arguments.Add(url);
		arguments.Add(target);

		var execution = await ExecuteAsync("git", arguments, parent, options.Timeout, runner, cancellationToken).ConfigureAwait(false);
		if (execution.ExitCode != 0)
		{
			// 失败时不要留下半个目录，否则后续安装会一直报「已存在」。
			TryDelete(target);
			return PackageResult.Failure($"git clone 失败（退出码 {execution.ExitCode}）", execution.Error);
		}

		return PackageResult.Success($"已安装 {spec.Value}", target);
	}

	private static async Task<(int ExitCode, string Output, string Error)> ExecuteAsync(
		string fileName,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		TimeSpan timeout,
		IPackageCommandRunner runner,
		CancellationToken cancellationToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		linked.CancelAfter(timeout);
		try
		{
			return await runner.RunAsync(fileName, arguments, workingDirectory, linked.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return (-1, string.Empty, $"命令超时（{timeout.TotalMinutes:0.#} 分钟）");
		}
	}

	private static (string? Url, string? Reference) SplitGitReference(string value)
	{
		var text = value.StartsWith("git:", StringComparison.Ordinal) ? value[4..] : value;
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

	private static bool IsRemote(string value) =>
		value.StartsWith("npm:", StringComparison.Ordinal) ||
		value.StartsWith("git:", StringComparison.Ordinal) ||
		value.StartsWith("http://", StringComparison.Ordinal) ||
		value.StartsWith("https://", StringComparison.Ordinal);

	private static string NpmExecutable(IReadOnlyList<string> command)
	{
		var executable = command.Count > 0 && command[0].Length > 0 ? command[0] : "npm";
		return OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".cmd" : executable;
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
