using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using Xunit;

namespace PiWeft.Ai.Tests;

/// 大段正文延迟存储：解析后只留 UTF-8，读出来还是原文。
public class DeferredTextTests
{
	private const int Threshold = TextContent.DeferredTextThreshold;

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(Threshold - 1)]
	[InlineData(Threshold)]
	[InlineData(Threshold + 1)]
	[InlineData(Threshold * 3)]
	public void TextBlock_Defers_OnlyAboveThreshold(int length)
	{
		var text = new string('a', length);

		var block = Assert.IsType<TextContent>(PiJsonValue.ReadBlock(ParseBlock(BlockLine(text))));

		Assert.Equal(length, block.TextLength);
		Assert.Equal(text, block.Text);
		// 阈值本身仍走字符串：多存一份字节不如直接留字符串。
		Assert.Equal(length > Threshold, block.IsTextDeferred);
	}

	[Fact]
	public void DeferredText_KeepsSignature_AndDecodesEveryRead()
	{
		var text = new string('x', Threshold * 2) + "尾巴";
		var line = BlockLine(text, signature: "sig-1");

		var block = Assert.IsType<TextContent>(PiJsonValue.ReadBlock(ParseBlock(line)));

		Assert.True(block.IsTextDeferred);
		Assert.Equal("sig-1", block.TextSignature);
		Assert.Equal(text, block.Text);
		// 不常驻缓存：第二次读走同一条解码路径，值不变、也不多留一份 UTF-16。
		Assert.Equal(text, block.Text);
		Assert.Equal(text.Length, block.TextLength);
	}

	[Fact]
	public void DeferredText_PreservesSurrogatePairs()
	{
		var lineText = "中文输出 🚀 混排 text 👩‍👩‍👧‍👦 ok\r\n";
		var text = string.Concat(Enumerable.Repeat(lineText, 512));

		var block = Assert.IsType<TextContent>(PiJsonValue.ReadBlock(ParseBlock(BlockLine(text))));

		Assert.True(text.Length > Threshold);
		Assert.True(block.IsTextDeferred);
		Assert.Equal(text.Length, block.TextLength);
		Assert.Equal(text, block.Text);
	}

	[Fact]
	public void LoneSurrogate_FallsBackToString()
	{
		// 孤立代理项编成 UTF-8 会变成 U+FFFD，回来就不是同一段文字了。直接用内存里的
		// JsonObject 造这个输入：经过 JSON 文本的路径上 System.Text.Json 已经替换过了。
		var text = new string('a', Threshold) + "\ud800";

		var block = Assert.IsType<TextContent>(PiJsonValue.ReadBlock(
			new JsonObject { ["type"] = "text", ["text"] = text }));

		Assert.False(block.IsTextDeferred);
		Assert.Equal(text, block.Text);
	}

	[Fact]
	public void PairedSurrogateAfterPlainText_StillDefers()
	{
		var text = new string('a', Threshold) + "🚀";

		var block = Assert.IsType<TextContent>(PiJsonValue.ReadBlock(ParseBlock(BlockLine(text))));

		Assert.True(block.IsTextDeferred);
		Assert.Equal(text, block.Text);
	}

	[Fact]
	public void DeferredText_EqualsInlineText_AndHashesAlike()
	{
		var text = new string('重', Threshold * 2);
		var deferred = TextContent.FromUtf8(Encoding.UTF8.GetBytes(text), text.Length);
		var inline = new TextContent { Text = text };

		Assert.True(deferred.IsTextDeferred);
		Assert.Equal(inline, deferred);
		Assert.Equal(inline.GetHashCode(), deferred.GetHashCode());
		Assert.Equal(inline with { TextSignature = "sig" }, deferred with { TextSignature = "sig" });
	}

	[Fact]
	public void DeferredText_Defaults_AreEmpty()
	{
		var block = new TextContent();

		Assert.Equal(string.Empty, block.Text);
		Assert.Equal(0, block.TextLength);
		Assert.False(block.IsTextDeferred);
	}

	[Fact]
	public void WithExpression_ReplacesDeferredStorage()
	{
		var text = new string('a', Threshold * 2);
		var block = TextContent.FromUtf8(Encoding.UTF8.GetBytes(text), text.Length);

		// 流式追加走 with { Text = … }：换掉正文就必须丢掉旧的 UTF-8，否则读到的是旧内容。
		var appended = block with { Text = block.Text + "!" };

		Assert.False(appended.IsTextDeferred);
		Assert.Equal(text + "!", appended.Text);
		Assert.Equal(text.Length + 1, appended.TextLength);
	}

	[Fact]
	public void MessageRoundTrip_LongToolResult_IsByteIdentical()
	{
		var text = string.Concat(Enumerable.Range(0, 4000).Select(static index => $"line {index} 输出\n"));
		var message = new ToolResultMessage
		{
			ToolCallId = "call-1",
			ToolName = "bash",
			Content = [new TextContent { Text = text }],
			Timestamp = 1_700_000_000_000,
		};

		var json = PiJson.SerializeMessage(message);
		var back = Assert.IsType<ToolResultMessage>(PiJson.DeserializeMessage(json));
		var block = Assert.IsType<TextContent>(Assert.Single(back.Content));

		Assert.True(block.IsTextDeferred);
		Assert.Equal(text, block.Text);
		Assert.Equal(json, PiJson.SerializeMessage(back));
	}

	private static string BlockLine(string text, string? signature = null)
	{
		var block = new JsonObject { ["type"] = "text", ["text"] = text };
		if (signature is not null)
		{
			block["textSignature"] = signature;
		}

		// 再过一遍 JSON 文本：会话文件读进来的就是这一形状。
		return block.ToJsonString();
	}

	private static JsonObject ParseBlock(string line) => Assert.IsType<JsonObject>(JsonNode.Parse(line));
}
