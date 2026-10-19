using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

public sealed class SessionCatalogTests
{
	[Fact]
	public void ScansSessionMessageAndInfoWithoutBuildingADocument()
	{
		var session = SessionCatalog.ScanLineForTest("""
			{"type":"session","version":3,"id":"s","timestamp":"t","cwd":"/tmp/pi"}
			""");
		Assert.Equal("/tmp/pi", session.Cwd);

		var info = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i","parentId":"s","timestamp":"t","name":"Checkout fix"}
			""");
		Assert.Equal("Checkout fix", info.Name);

		var user = SessionCatalog.ScanLineForTest("""
			{"type":"message","id":"u","parentId":"s","timestamp":"t","message":{"role":"user","content":"first line\nsecond line","timestamp":1}}
			""");
		Assert.Equal(1, user.Messages);
		Assert.Equal("first line second line", user.Preview);

		var assistant = SessionCatalog.ScanLineForTest("""
			{"type":"message","id":"a","parentId":"u","timestamp":"t","message":{"role":"assistant","content":[{"type":"text","text":"answer"},{"type":"toolCall","id":"c"}],"timestamp":2}}
			""");
		Assert.Equal(1, assistant.Messages);
		Assert.Null(assistant.Preview);
	}

	[Fact]
	public void CountsToolResultsAndSkipsTheirLargeContent()
	{
		var line = """
			{"type":"message","id":"r","parentId":"a","timestamp":"t","message":{"role":"toolResult","toolCallId":"c","toolName":"bash","content":[{"type":"text","text":"a very long tool output"}],"timestamp":3}}
			""";
		var scan = SessionCatalog.ScanLineForTest(line);
		Assert.Equal(1, scan.Messages);
		Assert.Null(scan.Preview);
	}

	[Fact]
	public void HandlesContentBeforeRoleAndMalformedLines()
	{
		var line = """
			{"type":"message","id":"u","message":{"content":[{"type":"text","text":"preview"}],"role":"user"},"timestamp":"t"}
			""";
		var scan = SessionCatalog.ScanLineForTest(line);
		Assert.Equal(1, scan.Messages);
		Assert.Equal("preview", scan.Preview);

		var malformed = SessionCatalog.ScanLineForTest("""{"type":"message","message":{"role":"user","content":"unterminated"}""");
		Assert.Equal(0, malformed.Messages);
	}

	[Fact]
	public void EmptyFirstUserTextDoesNotHideTheNextUserPreview()
	{
		var scan = SessionCatalog.ScanLinesForTest([
			"""{"type":"message","message":{"role":"user","content":"   "}}""",
			"""{"type":"message","message":{"role":"user","content":"real preview"}}""",
		]);
		Assert.Equal(2, scan.Messages);
		Assert.Equal("real preview", scan.Preview);
	}

	[Fact]
	public void MalformedTrailingTokenDoesNotPartiallyApplyTheLine()
	{
		var scan = SessionCatalog.ScanLinesForTest([
			"""{"type":"message","message":{"role":"user","content":"kept"}}""",
			"""{"type":"message","message":{"role":"assistant","content":"dropped"}} trailing""",
		]);
		Assert.Equal(1, scan.Messages);
		Assert.Equal("kept", scan.Preview);
	}

	[Fact]
	public void LaterSessionInfoClearsThePreviousName()
	{
		var first = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i1","parentId":"s","timestamp":"t","name":"First"}
			""");
		Assert.Equal("First", first.Name);

		var second = SessionCatalog.ScanLineForTest("""
			{"type":"session_info","id":"i2","parentId":"i1","timestamp":"t"}
			""");
		Assert.Equal(string.Empty, second.Name);
	}
}
