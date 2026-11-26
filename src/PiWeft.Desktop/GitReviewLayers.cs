namespace PiWeft.Desktop;

/// 改动文件在审查里的三个层：接口（契约、声明、清单）、实现、测试（含快照与锁文件）。
/// 比较节按它筛选，避免几百个文件的比较列表把真正要读的实现埋在测试与生成物里。
public enum GitReviewLayer
{
	/// <summary>不按层过滤。</summary>
	All,

	/// <summary>实现代码，比较列表的默认主体。</summary>
	Implementation,

	/// <summary>契约与声明：清单、项目文件、接口定义。</summary>
	Interface,

	/// <summary>测试、快照、夹具与锁文件。</summary>
	Tests,
}

/// 按路径给文件分层。规则跟着各语言常见约定走，不做内容分析：
/// 目录名与文件名后缀就够覆盖绝大多数仓库，读文件内容会随比较次数放大成本。
/// 仓内自定义（`.gitattributes`）留到需要时再加，本版只用内建规则。
internal static class GitReviewLayers
{
	// 目录名（小写比较）：测试、快照、夹具目录。
	private static readonly HashSet<string> TestDirectories = new(StringComparer.OrdinalIgnoreCase)
	{
		"test", "tests", "__tests__", "spec", "specs", "__snapshots__", "snapshots",
		"testdata", "test-data", "fixtures", "e2e", "integration-tests", "unittests", "unit-tests",
	};

	// 文件名（小写比较）：锁文件按 dsh 的口径归到测试层——审查实现时它们只是噪声。
	private static readonly HashSet<string> TestFiles = new(StringComparer.OrdinalIgnoreCase)
	{
		"package-lock.json", "pnpm-lock.yaml", "yarn.lock", "cargo.lock", "poetry.lock", "go.sum",
		"packages.lock.json", "gemfile.lock", "composer.lock", "uv.lock", "bun.lockb",
	};

	// 清单与声明文件（小写比较）。
	private static readonly HashSet<string> InterfaceFiles = new(StringComparer.OrdinalIgnoreCase)
	{
		"package.json", "cargo.toml", "pyproject.toml", "go.mod", "go.work", "setup.cfg", "setup.py",
		"pom.xml", "composer.json", "gemfile", "makefile", "cmakelists.txt", "global.json",
		"directory.build.props", "directory.build.targets", "directory.packages.props",
	};

	private static readonly string[] InterfaceSuffixes =
	[
		".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".sln", ".slnx",
		".d.ts", ".proto", ".graphql", ".gql", ".h", ".hpp", ".hh", ".hxx",
		".idl", ".thrift", ".api", ".manifest", ".editorconfig",
	];

	private static readonly string[] TestSuffixes =
	[
		"_test.go", "_test.py", "_test.rb", "_test.rs", "_spec.rb",
		".test.ts", ".test.tsx", ".test.js", ".test.jsx", ".test.mjs", ".test.cjs",
		".spec.ts", ".spec.tsx", ".spec.js", ".spec.jsx", ".spec.mjs", ".spec.cjs",
		".snap", ".snapshot",
	];

	public static string Label(GitReviewLayer layer) => layer switch
	{
		GitReviewLayer.Implementation => Strings.GitReviewLayerImplementation,
		GitReviewLayer.Interface => Strings.GitReviewLayerInterface,
		GitReviewLayer.Tests => Strings.GitReviewLayerTests,
		_ => Strings.GitReviewLayerAll,
	};

	/// 仓内 `.gitattributes` 先算：显式层覆盖一切，`linguist-generated` 归测试；没命中再走内置规则。
	public static GitReviewLayer For(string path, GitReviewRules? rules)
	{
		if (rules is { IsEmpty: false } && rules.LayerFor(path) is { } custom)
		{
			return custom;
		}

		return For(path);
	}

	public static GitReviewLayer For(string path)
	{
		var normalized = path.Replace('\\', '/');
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
		{
			return GitReviewLayer.Implementation;
		}

		// 目录层：tests/、__snapshots__/，以及 Foo.Tests 这类项目目录。
		for (var index = 0; index < segments.Length - 1; index++)
		{
			var directory = segments[index];
			if (TestDirectories.Contains(directory) || LooksLikeTestDirectory(directory))
			{
				return GitReviewLayer.Tests;
			}
		}

		var file = segments[^1];
		var lower = file.ToLowerInvariant();
		if (TestFiles.Contains(lower))
		{
			return GitReviewLayer.Tests;
		}

		// C# / Swift / Kotlin 的测试文件名靠大写 T 区分（FooTests.cs），不能拿小写后缀判：
		// "latest.cs" 在忽略大小写时也以 "test.cs" 结尾。
		if (file.EndsWith("Tests.cs", StringComparison.Ordinal) ||
			file.EndsWith("Test.cs", StringComparison.Ordinal) ||
			file.EndsWith("Tests.swift", StringComparison.Ordinal) ||
			file.EndsWith("Test.swift", StringComparison.Ordinal) ||
			file.EndsWith("Tests.kt", StringComparison.Ordinal) ||
			file.EndsWith("Test.kt", StringComparison.Ordinal) ||
			file.EndsWith("Tests.java", StringComparison.Ordinal) ||
			file.EndsWith("Test.java", StringComparison.Ordinal))
		{
			return GitReviewLayer.Tests;
		}

		foreach (var suffix in TestSuffixes)
		{
			if (lower.EndsWith(suffix, StringComparison.Ordinal))
			{
				return GitReviewLayer.Tests;
			}
		}

		if (InterfaceFiles.Contains(lower))
		{
			return GitReviewLayer.Interface;
		}

		foreach (var suffix in InterfaceSuffixes)
		{
			if (lower.EndsWith(suffix, StringComparison.Ordinal))
			{
				return GitReviewLayer.Interface;
			}
		}

		if ((lower.StartsWith("openapi", StringComparison.Ordinal) || lower.StartsWith("swagger", StringComparison.Ordinal)) &&
			(lower.EndsWith(".yaml", StringComparison.Ordinal) || lower.EndsWith(".yml", StringComparison.Ordinal) ||
			 lower.EndsWith(".json", StringComparison.Ordinal)))
		{
			return GitReviewLayer.Interface;
		}

		return GitReviewLayer.Implementation;
	}

	private static bool LooksLikeTestDirectory(string directory)
	{
		var lower = directory.ToLowerInvariant();
		return lower.EndsWith("tests", StringComparison.Ordinal) ||
			lower.EndsWith("-test", StringComparison.Ordinal) ||
			lower.EndsWith("_test", StringComparison.Ordinal) ||
			lower.EndsWith(".tests", StringComparison.Ordinal) ||
			lower.EndsWith(".specs", StringComparison.Ordinal);
	}
}
