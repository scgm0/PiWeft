using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiWeft.Ai;
using PiWeft.Ai.Json;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 延迟存储的正文走 `JsonUtf8String` 直接写 UTF-8，普通正文走 `Utf8JsonWriter.WriteStringValue`。
/// 两条路径必须逐字节一致：会话文件是与 pi 互通的产物，转义差一个字符就是另一份历史。
public sealed class TextWriteEscapeTests
{
	[Theory]
	[InlineData("a\u0001b")]
	[InlineData("a\u0000b")]
	[InlineData("a\u001fb")]
	[InlineData("a\u007fb")]
	[InlineData("a\u0080b")]
	[InlineData("a\u009fb")]
	[InlineData("a\u2028b")]
	[InlineData("a\u2029b")]
	[InlineData("a\"b\\c")]
	[InlineData("中文")]
	[InlineData("中文😀")]
	[InlineData("plain text\nwith newline")]
	public void DeferredTextWritesTheSameBytesAsInlineText(string sample)
	{
		var inline = Serialize(new TextContent { Text = sample });
		var deferred = Serialize(TextContent.FromUtf8(Encoding.UTF8.GetBytes(sample), sample.Length));

		Assert.Equal(inline, deferred);
	}

	private static string Serialize(TextContent block)
	{
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		}))
		{
			PiJsonValue.WriteBlock(writer, block);
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
