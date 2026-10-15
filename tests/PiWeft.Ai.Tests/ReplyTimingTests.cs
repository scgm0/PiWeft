using PiWeft.Ai;
using PiWeft.Ai.Json;
using Xunit;

namespace PiWeft.Ai.Tests;

public sealed class ReplyTimingTests
{
	[Fact]
	public async Task TextDelayAndStreamingDurationAreMeasuredAndRoundTrip()
	{
		var stream = new AssistantMessageEventStream();
		var message = new AssistantMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Partial = message, Delta = "" });
		await Task.Delay(15);
		stream.Push(new ThinkingDeltaEvent { ContentIndex = 0, Partial = message, Delta = "thinking" });
		await Task.Delay(15);
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Partial = message, Delta = "answer" });
		await Task.Delay(15);
		stream.Push(new DoneEvent { Partial = message, Reason = StopReason.Stop });
		var result = await stream.Result;
		Assert.True(result.DurationMs > 0);
		Assert.True(result.FirstTokenMs > 0);
		Assert.True(result.StreamingDurationMs > 0);
		Assert.True(result.FirstTokenMs <= result.DurationMs);
		Assert.True(result.StreamingDurationMs <= result.DurationMs);
		// TTFT uses the first text delta, but streaming speed includes prior thinking.
		Assert.True(result.FirstTokenMs + result.StreamingDurationMs > result.DurationMs);
		var restored = Assert.IsType<AssistantMessage>(PiJson.DeserializeMessage(PiJson.SerializeMessage(result)));
		Assert.Equal(result.DurationMs, restored.DurationMs);
		Assert.Equal(result.FirstTokenMs, restored.FirstTokenMs);
		Assert.Equal(result.StreamingDurationMs, restored.StreamingDurationMs);
	}

	[Fact]
	public void ThinkingOnlyFallsBackToThinkingAndEndAlsoMeasures()
	{
		var stream = new AssistantMessageEventStream();
		var message = new AssistantMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
		stream.Push(new ThinkingDeltaEvent { ContentIndex = 0, Partial = message, Delta = "plan" });
		stream.End(message);
		Assert.NotNull(message.FirstTokenMs);
		Assert.NotNull(message.StreamingDurationMs);
		// GetElapsedTime returns TimeSpan ticks (100 ns), so independently rounded intervals can differ.
		Assert.InRange(Math.Abs(message.DurationMs!.Value - message.FirstTokenMs!.Value - message.StreamingDurationMs!.Value), 0, 0.001);
	}

	[Fact]
	public void MissingHistoricalTimingIsNotInventedAndNoDeltaHasNoSpeed()
	{
		var historical = new AssistantMessage { Timestamp = 1, DurationMs = 2000 };
		var oldStream = new AssistantMessageEventStream();
		oldStream.Push(new TextDeltaEvent { ContentIndex = 0, Partial = historical, Delta = "old" });
		oldStream.End(historical);
		Assert.Equal(2000, historical.DurationMs);
		Assert.Null(historical.FirstTokenMs);
		Assert.Null(historical.StreamingDurationMs);
		var current = new AssistantMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
		new AssistantMessageEventStream().End(current);
		Assert.NotNull(current.DurationMs);
		Assert.Null(current.FirstTokenMs);
		Assert.Null(current.StreamingDurationMs);
	}

	[Fact]
	public void ErrorAndLateDeltasDoNotOverwriteFinalTiming()
	{
		var stream = new AssistantMessageEventStream();
		var message = new AssistantMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), StopReason = StopReason.Error };
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Partial = message, Delta = "partial" });
		stream.Push(new ErrorEvent { Partial = message, Reason = StopReason.Error });
		var first = message.FirstTokenMs;
		var duration = message.DurationMs;
		stream.Push(new TextDeltaEvent { ContentIndex = 0, Partial = message, Delta = "late" });
		stream.End(message);
		Assert.NotNull(first);
		Assert.Equal(first, message.FirstTokenMs);
		Assert.Equal(duration, message.DurationMs);
	}
}
