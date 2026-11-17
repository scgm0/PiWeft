using PiWeft.Cli;
using Xunit;
using ZLogger;

namespace PiWeft.Cli.Tests;

[Collection("process-environment")]
public sealed class AppLogTests
{
	[Fact]
	public void FileLogsFlushOnShutdownWithoutPersistingRawExceptionPayloads()
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
					new InvalidOperationException("sk-secret-provider-payload", new Exception("disk full")));
			}

			var text = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "logs"), "piweft-test-*.log").Select(File.ReadAllText));
			Assert.Contains("Completed count=42", text);
			Assert.Contains("Test failure", text);
			Assert.Contains(nameof(InvalidOperationException), text);
			// 凭据形态的消息必须脱敏：这里断言的是「消息保留但不原样落盘」。
			Assert.DoesNotContain("sk-secret-provider-payload", text);
			// 脱敏后保留消息与内部异常，否则日志只剩一个异常类型，看不出失败原因。
			Assert.Contains("disk full", text);
			Assert.Contains("System.InvalidOperationException: ***", text);
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

	[Fact]
	public void SetMinimumLevelTakesEffectWithoutRestartingTheFactory()
	{
		var root = Path.Combine(Path.GetTempPath(), "pi-log-" + Guid.NewGuid().ToString("N"));
		var previous = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
		var previousLevel = Environment.GetEnvironmentVariable("PI_LOG_LEVEL");
		try
		{
			Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", root);
			Environment.SetEnvironmentVariable("PI_LOG_LEVEL", null);
			using (AppLog.Start("test"))
			{
				var logger = AppLog.For<AppLogTests>();
				logger.ZLogDebug($"debug-before");
				AppLog.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
				logger.ZLogDebug($"debug-after");
				AppLog.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
				logger.ZLogDebug($"debug-hidden-again");
			}

			var text = string.Concat(Directory.EnumerateFiles(Path.Combine(root, "logs"), "piweft-test-*.log").Select(File.ReadAllText));
			Assert.DoesNotContain("debug-before", text);
			Assert.Contains("debug-after", text);
			Assert.DoesNotContain("debug-hidden-again", text);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previous);
			Environment.SetEnvironmentVariable("PI_LOG_LEVEL", previousLevel);
			AppLog.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}

	[Fact]
	public void SingleRecordStaysOnOneLine()
	{
		var redactor = new LogRedactor("/tmp/piweft-cwd", "/tmp/piweft-agent");
		var line = redactor.Line("first\nsecond\r\nthird");
		Assert.Equal("first second third", line);
	}

	[Fact]
	public void LongMessageIsTruncated()
	{
		var redactor = new LogRedactor("/tmp/piweft-cwd", "/tmp/piweft-agent");
		var line = redactor.Line(new string('x', LogRedactor.MaxLineChars + 100));
		Assert.StartsWith(new string('x', 64), line, StringComparison.Ordinal);
		Assert.Contains("…(+100)", line, StringComparison.Ordinal);
	}
}
