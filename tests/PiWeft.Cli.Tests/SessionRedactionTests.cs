using System.Text.Json.Nodes;
using PiWeft.Cli;
using Xunit;

namespace PiWeft.Cli.Tests;

/// 安全导出的脱敏规则：常见令牌、赋值形态、Bearer、URL 查询参数、私钥块与路径前缀。
public sealed class SessionRedactionTests
{
	private static readonly RedactionScope Scope = new("/tmp/piweft-project", "/home/piweft-user");

	[Fact]
	public void TextRedactsCredentialsAndPaths()
	{
		var text = """
			key sk-aaaaaaaaaaaaaaaaaaaaaaaa
			password = "hunter2xyz"
			Authorization: Bearer abc.def
			https://example.com/x?token=abc123&ok=1
			path /tmp/piweft-project/src and /home/piweft-user/notes
			-----BEGIN OPENSSH PRIVATE KEY-----
			abc
			-----END OPENSSH PRIVATE KEY-----
			""";

		var redacted = SessionRedaction.Text(text, Scope);

		Assert.DoesNotContain("sk-", redacted);
		Assert.DoesNotContain("hunter2xyz", redacted);
		Assert.DoesNotContain("abc.def", redacted);
		Assert.DoesNotContain("token=abc123", redacted);
		Assert.DoesNotContain("/tmp/piweft-project", redacted);
		Assert.DoesNotContain("/home/piweft-user", redacted);
		Assert.DoesNotContain("BEGIN OPENSSH", redacted);
		Assert.Contains("<workspace>/src", redacted);
		Assert.Contains("~/notes", redacted);
	}

	[Fact]
	public void PathPrefixOnlyMatchesWholeSegments()
	{
		var redacted = SessionRedaction.Text("/home/piweft-user2/file /home/piweft-user/file", Scope);

		Assert.Contains("/home/piweft-user2/file", redacted);
		Assert.Contains("~/file", redacted);
	}

	[Fact]
	public void JsonWalksNestedStrings()
	{
		var redacted = (JsonObject)SessionRedaction.Json(new JsonObject
		{
			["note"] = "token=supersecretvalue",
			["items"] = new JsonArray("sk-bbbbbbbbbbbbbbbbbbbbbbbb", 7),
		}, Scope)!;

		Assert.DoesNotContain("supersecretvalue", redacted.ToJsonString());
		Assert.DoesNotContain("sk-bbbbbbbbbbbbbbbbbbbbbbbb", redacted.ToJsonString());
		Assert.Equal(7, redacted["items"]![1]!.GetValue<int>());
	}
}
