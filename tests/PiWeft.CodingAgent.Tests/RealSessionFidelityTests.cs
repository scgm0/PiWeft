using System.Text;
using System.Text.Json.Nodes;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// <summary>
/// Reads session files written by the real pi binary and re-serializes every line, so a format drift shows up as a
/// concrete diff instead of a silent incompatibility. Skips when no pi sessions exist on the machine.
/// </summary>
public class RealSessionFidelityTests
{
	private static string? SessionsRoot()
	{
		var root = Path.Combine(SessionPaths.GetAgentDir(), "sessions");
		return Directory.Exists(root) ? root : null;
	}

	private static List<string> SessionFiles(string root) =>
		[.. Directory.EnumerateFiles(root, "*" + SessionFormat.Extension, SearchOption.AllDirectories)];

	[Fact]
	public void EveryLineOfEveryRealSessionRoundTrips()
	{
		var root = SessionsRoot();
		if (root is null)
		{
			return; // no pi installation on this machine
		}

		var failures = new List<string>();
		var totalLines = 0;
		var parsedEntries = 0;

		foreach (var path in SessionFiles(root))
		{
			foreach (var line in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}

				totalLines++;

				object? parsed;
				try
				{
					parsed = SessionJson.ParseLine(line);
				}
				catch (Exception ex)
				{
					failures.Add($"{Path.GetFileName(path)}: parse threw {ex.GetType().Name}: {ex.Message}\n  {Trim(line)}");
					continue;
				}

				if (parsed is null)
				{
					failures.Add($"{Path.GetFileName(path)}: parsed to null\n  {Trim(line)}");
					continue;
				}

				parsedEntries++;

				var reserialized = parsed switch
				{
					SessionHeader header => SessionJson.Serialize(header),
					SessionEntry entry => SessionJson.Serialize(entry),
					_ => throw new InvalidOperationException($"unexpected parse result {parsed.GetType().Name}"),
				};

				if (!JsonNode.DeepEquals(JsonNode.Parse(line), JsonNode.Parse(reserialized)))
				{
					failures.Add(
						$"{Path.GetFileName(path)}: round trip changed the payload\n{FirstDifference(line, reserialized)}\n  in : {Trim(line)}\n  out: {Trim(reserialized)}");
				}
			}
		}

		Assert.True(totalLines > 0, "expected at least one real session line");
		Assert.True(parsedEntries == totalLines, $"only {parsedEntries} of {totalLines} lines parsed");
		Assert.True(
			failures.Count == 0,
			$"{failures.Count} of {totalLines} lines did not round-trip:\n\n{string.Join("\n\n", failures.Take(5))}");
	}

	[Fact]
	public void RealSessionsBuildAValidContext()
	{
		var root = SessionsRoot();
		if (root is null)
		{
			return;
		}

		var checkedAny = false;
		foreach (var path in SessionFiles(root))
		{
			var manager = SessionManager.Open(path);

			// 外部工具（例如 PiDeck 的工作区）会写出没有 session 头的会话文件；
			// 它们不是本端口的产物，跳过即可 —— 修复逻辑单独有测试。
			if (manager.Header is null)
			{
				continue;
			}

			var context = manager.BuildSessionContext();
			checkedAny = true;

			Assert.Equal(3, manager.Header!.Version);
			Assert.NotEmpty(manager.Cwd);

			// Every context message must be a role a provider can send.
			foreach (var message in context.Messages)
			{
				Assert.Contains(
					message.Role,
					new[] { "system", "user", "assistant", "toolResult", "custom", "bashExecution", "branchSummary", "compactionSummary" });
			}

			// The picker fields must be populated for a session that has a user message.
			var info = manager.ToSessionInfo();
			Assert.Equal(manager.SessionId, info.Id);
			Assert.Equal(path, info.Path);
		}

		Assert.True(checkedAny);
	}

	private static string FirstDifference(string a, string b)
	{
		var limit = Math.Min(a.Length, b.Length);
		for (var i = 0; i < limit; i++)
		{
			if (a[i] != b[i])
			{
				return $"  first difference at {i}: in='{Window(a, i)}' out='{Window(b, i)}'";
			}
		}

		return a.Length == b.Length
			? "  (strings are identical but the parsed nodes differ)"
			: $"  lengths differ: in={a.Length} out={b.Length}";
	}

	private static string Window(string value, int index)
	{
		var start = Math.Max(0, index - 40);
		var length = Math.Min(80, value.Length - start);
		return value.Substring(start, length);
	}

	private static string Trim(string value)
	{
		var builder = new StringBuilder(value.Length);
		builder.Append(value.AsSpan(0, Math.Min(value.Length, 600)));
		return builder.ToString();
	}
}
