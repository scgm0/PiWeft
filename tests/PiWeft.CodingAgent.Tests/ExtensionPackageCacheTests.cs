using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Extensions;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <c>-e npm:</c> / <c>-e git:</c> 的物化：离线时只认缓存，失败要进诊断而不是静默跳过。
[Collection("process-environment")]
public sealed class ExtensionPackageCacheTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-extpkg-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string? _offline = Environment.GetEnvironmentVariable(PiEnvironment.OfflineVariable);

	public ExtensionPackageCacheTests()
	{
		Directory.CreateDirectory(_root);
		ExtensionPackageCache.Invalidate();
	}

	public void Dispose()
	{
		ExtensionPackageCache.Invalidate();
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, _offline);
		PiEnvironment.Configure();
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void NpmSpecIsRecognized()
	{
		Assert.True(ExtensionPackageCache.IsPackageSpec("npm:@scope/pkg@1.0.0"));
		Assert.True(ExtensionPackageCache.IsPackageSpec("git:github.com/org/repo@v1"));
		Assert.False(ExtensionPackageCache.IsPackageSpec("./local/dir"));
		Assert.False(ExtensionPackageCache.IsPackageSpec("builtin:codemode"));
	}

	[Fact]
	public void OfflineWithoutCacheReportsInsteadOfLoading()
	{
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, "1");
		PiEnvironment.Configure();
		ExtensionPackageCache.Invalidate();

		var root = ExtensionPackageCache.Resolve("npm:@scope/missing", _root, out var error);

		Assert.Null(root);
		Assert.NotNull(error);
	}

	[Fact]
	public void CachedNpmPackageIsUsedOffline()
	{
		var target = Path.Combine(_root, "extension-packages", "npm", "node_modules", "@scope", "pkg");
		Directory.CreateDirectory(target);
		File.WriteAllText(Path.Combine(target, "index.js"), "module.exports = function (pi) {};");
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, "1");
		PiEnvironment.Configure();
		ExtensionPackageCache.Invalidate();

		var root = ExtensionPackageCache.Resolve("npm:@scope/pkg@1.2.3", _root, out var error);

		Assert.Null(error);
		Assert.Equal(target, root);
	}

	[Fact]
	public void DiscoveryLoadsExtensionsFromACachedPackage()
	{
		var target = Path.Combine(_root, "extension-packages", "npm", "node_modules", "@scope", "pkg");
		Directory.CreateDirectory(Path.Combine(target, "extensions"));
		File.WriteAllText(
			Path.Combine(target, "extensions", "hello.js"),
			"module.exports = function (pi) { pi.registerCommand('hello', 'greet', function () {}); };");
		Environment.SetEnvironmentVariable(PiEnvironment.OfflineVariable, "1");
		PiEnvironment.Configure();
		ExtensionPackageCache.Invalidate();

		var descriptors = ExtensionDescriptor.Discover(
			Path.Combine(_root, "project"),
			trusted: true,
			_root,
			["npm:@scope/pkg@1.2.3"],
			includeDiscovered: false);

		var descriptor = Assert.Single(descriptors);
		Assert.True(descriptor.Supported, descriptor.UnsupportedReason);
		Assert.Equal("hello.js", Path.GetFileName(descriptor.FilePath));
	}
}
