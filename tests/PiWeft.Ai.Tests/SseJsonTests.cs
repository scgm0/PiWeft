using System.Text;
using PiWeft.Ai;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class SseJsonTests
{
	[Fact]
	public void ParsesAPayloadThroughAReusableReader()
	{
		var bytes = Encoding.UTF8.GetBytes("""{"type":"delta","text":"hello"}""");
		var reader = new SseJsonReader();
		using var document = reader.Parse("delta", bytes);
		Assert.Equal("delta", document.RootElement.GetProperty("type").GetString());
		Assert.Equal("hello", document.RootElement.GetProperty("text").GetString());

		// The second parse reuses the same buffer and must not see stale bytes.
		var next = Encoding.UTF8.GetBytes("""{"type":"message","text":"bye"}""");
		using var second = reader.Parse("message", next);
		Assert.Equal("bye", second.RootElement.GetProperty("text").GetString());
	}

	[Fact]
	public void MalformedPayloadNamesTheProviderEvent()
	{
		var reader = new SseJsonReader();
		var error = Assert.Throws<InvalidOperationException>(() =>
			reader.Parse("message_start", Encoding.UTF8.GetBytes("{bad")));
		Assert.Contains("message_start", error.Message);
	}
}
