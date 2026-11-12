using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Compatibility;
using PiWeft.CodingAgent.Session;
using PiWeft.CodingAgent.Subagents;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 注入消息的落盘与识别：customType/display 必须原样往返，否则重启后回报会冒充用户提问。
public sealed class InjectedMessageRoundTripTests
{
	[Fact]
	public void CustomTypeAndDisplaySurviveASessionRoundTrip()
	{
		var root = Path.Combine(Path.GetTempPath(), "piweft-roundtrip-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		try
		{
			var manager = SessionManager.Create(root, Path.Combine(root, "sessions"));
			var file = manager.SessionFile;
			Assert.NotNull(file);

			manager.AppendMessage(new UserMessage { Content = MessageContent.FromText("真正的提问。") });
			manager.AppendMessage(new UserMessage
			{
				Content = MessageContent.FromText("子代理「scout-1」完成了一轮。\n结论。"),
				CustomType = SubagentHub.ResultMessageType,
			});
			manager.AppendMessage(new UserMessage
			{
				Content = MessageContent.FromText("隐藏的注入。"),
				CustomType = PiWeftProtocol.ContinueGenerationRole,
				Display = false,
			});

			// 落盘的就是 pi 的形状：customType 在，display:false 在。
			var text = File.ReadAllText(file);
			Assert.Contains($"\"customType\":\"{SubagentHub.ResultMessageType}\"", text);
			Assert.Contains("\"display\":false", text);

			var reopened = SessionManager.Open(file);
			var messages = reopened.BuildProjectedMessages().OfType<UserMessage>().ToArray();
			Assert.Equal(3, messages.Length);
			Assert.True(messages[0].IsUserAuthored);
			Assert.False(messages[1].IsUserAuthored);
			Assert.Equal(SubagentHub.ResultMessageType, messages[1].CustomType);
			Assert.False(messages[2].Display);
			Assert.False(messages[2].IsUserAuthored);

			// 重开后分发点、标题与重试都只认用户真正写的那条。
			Assert.Equal("真正的提问。", Assert.Single(reopened.GetUserMessagesForForking()).Text);
			Assert.Equal("真正的提问。", reopened.FirstUserMessageText());
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void PlainUserMessagesStayByteIdenticalToPi()
	{
		var entry = new SessionMessageEntry
		{
			Id = "a1",
			Timestamp = "2026-01-01T00:00:00.000Z",
			Message = new UserMessage { Content = MessageContent.FromText("hi"), Timestamp = 1 },
		};

		var json = SessionJson.Serialize(entry);
		// 普通用户消息不带 customType，也不写 display:true：与 pi 写出的行一致。
		Assert.DoesNotContain("customType", json);
		Assert.DoesNotContain("display", json);

		var parsed = Assert.IsType<SessionMessageEntry>(SessionLines.Entry(SessionJson.ParseLine(json)));
		var user = Assert.IsType<UserMessage>(parsed.Message);
		Assert.True(user.Display);
		Assert.True(user.IsUserAuthored);
		Assert.Empty(user.Extra ?? new JsonObject());
	}
}
