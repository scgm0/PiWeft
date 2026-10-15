using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>D-02：项目文件列举、读取与写入的边界。</summary>
public class ProjectFilesTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-files-" + Guid.NewGuid().ToString("N")[..8]);

	public ProjectFilesTests()
	{
		Directory.CreateDirectory(_root);
		Directory.CreateDirectory(Path.Combine(_root, "src"));
		Directory.CreateDirectory(Path.Combine(_root, ".git"));
		Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
		Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
		File.WriteAllText(Path.Combine(_root, "README.md"), "readme");
		File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App {}");
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}

	[Fact]
	public void List_ShowsDirectoriesFirstAndSkipsIgnored()
	{
		var entries = ProjectFiles.List(_root, string.Empty);

		Assert.Equal(["src"], entries.Where(static entry => entry.IsDirectory).Select(static entry => entry.Name));
		Assert.Equal(["README.md"], entries.Where(static entry => !entry.IsDirectory).Select(static entry => entry.Name));
		Assert.DoesNotContain(entries, static entry => entry.Name is ".git" or "node_modules" or ".hidden");
	}

	[Fact]
	public void List_AttachesGitStatusMarkers()
	{
		var statuses = new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = "修改" };

		var entries = ProjectFiles.List(_root, string.Empty, statuses);

		Assert.Equal("修改", entries.Single(static entry => entry.Name == "README.md").Status);
	}

	[Fact]
	public void List_DescendsAndReportsRelativePaths()
	{
		var entries = ProjectFiles.List(_root, "src");

		var file = Assert.Single(entries);
		Assert.Equal("src/app.cs", file.RelativePath);
		Assert.False(file.IsDirectory);
	}

	[Fact]
	public void Read_ReturnsContentAndTicks()
	{
		var (content, ticks, error) = ProjectFiles.Read(_root, "README.md");

		Assert.Null(error);
		Assert.Equal("readme", content);
		Assert.True(ticks > 0);
	}

	[Fact]
	public void Read_RejectsBinaryOversizedAndMissingFiles()
	{
		File.WriteAllBytes(Path.Combine(_root, "bin.dat"), [1, 0, 2]);

		Assert.Equal(Strings.FileBrowserBinary, ProjectFiles.Read(_root, "bin.dat").Error);
		Assert.Equal(Strings.FileBrowserTooLarge, ProjectFiles.Read(_root, "README.md", maxBytes: 2).Error);
		Assert.Equal(Strings.FileBrowserMissing, ProjectFiles.Read(_root, "nope.txt").Error);
	}

	[Fact]
	public void Write_RoundTripsAndDetectsExternalChanges()
	{
		Assert.Null(ProjectFiles.Write(_root, "src/new.txt", "hello"));

		var (content, ticks, error) = ProjectFiles.Read(_root, "src/new.txt");
		Assert.Null(error);
		Assert.Equal("hello", content);

		// 时间戳已变化：旧期望值应被拒绝，避免覆盖。
		Assert.Equal(Strings.FileBrowserConflict, ProjectFiles.Write(_root, "src/new.txt", "other", expectedTicks: ticks - 1));
		Assert.Null(ProjectFiles.Write(_root, "src/new.txt", "other", expectedTicks: ticks));
		Assert.Equal("other", File.ReadAllText(Path.Combine(_root, "src", "new.txt")));
	}

	[Fact]
	public void Write_RejectsPathsEscapingTheProject()
	{
		Assert.Equal(Strings.FileBrowserInvalidPath, ProjectFiles.Write(_root, "../escape.txt", "x"));
		Assert.Equal(Strings.FileBrowserMissing, ProjectFiles.Read(_root, "../escape.txt").Error);
	}
}
