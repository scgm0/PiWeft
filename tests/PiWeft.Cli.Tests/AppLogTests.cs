using PiWeft.Cli;
using Xunit;
using ZLogger;

namespace PiWeft.Cli.Tests;

[Collection("process-environment")]
public sealed class AppLogTests
{
	[Fact]
	public void FileLogsFlushOnShutdownWithoutPersistingExceptionPayloads()
	{
		var root = Path.Combine(Path.GetTempPath(), "pi-log-" + Guid.NewGuid().ToString("N"));
		var previous = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
		var previousLevel = Environment.GetEnvironmentVariable("PI_LOG_LEVEL");
		try
		{
			Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", root);
			Environment.SetEnvironmentVariable("PI_LOG_LEVEL", "Information");
			using (AppLog.Start("test"))
			{
				AppLog.For<AppLogTests>().ZLogInformation($"Completed count={42}");
				AppLog.Failure(AppLog.For<AppLogTests>(), "Test failure",
					new InvalidOperationException("sk-secret-provider-payload", new Exception("private-prompt")));
			}

			var text = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "logs"), "piweft-test-*.log").Select(File.ReadAllText));
			Assert.Contains("Completed count=42", text);
			Assert.Contains("Test failure", text);
			Assert.Contains(nameof(InvalidOperationException), text);
			Assert.DoesNotContain("sk-secret-provider-payload", text);
			Assert.DoesNotContain("private-prompt", text);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previous);
			Environment.SetEnvironmentVariable("PI_LOG_LEVEL", previousLevel);
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}
}
