using System.Text.Json;

namespace PiWeft.Ai;

/// <summary>
/// Reuses one UTF-8 buffer for the events of a single SSE stream. A <see cref="JsonDocument"/>
/// still lives for one event, but no byte array is allocated or rented per event.
/// </summary>
internal sealed class SseJsonReader
{
	private byte[] _buffer = [];

	public JsonDocument Parse(string eventType, ReadOnlySpan<byte> data)
	{
		if (_buffer.Length < data.Length)
		{
			_buffer = new byte[Math.Max(data.Length, _buffer.Length * 2)];
		}

		data.CopyTo(_buffer);
		try
		{
			return JsonDocument.Parse(_buffer.AsMemory(0, data.Length));
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Could not parse provider SSE event '{eventType}': {ex.Message}",
				ex);
		}
	}
}
