using System.Text.Json;

namespace PiWeft.CodingAgent.Stats;

public static partial class UsageStats
{
	private readonly record struct UsageValues(
		long Input, long Output, long CacheRead, long CacheWrite, long ReportedTokens, double Cost);

	private struct MessageFields
	{
		public string? Provider;
		public string? Model;
		public string? Role;
		public UsageValues Usage;
		public bool HasUsage;
		public double DurationMs;
		public long Timestamp;
	}

	private readonly record struct UsageLineSample(
		string Provider,
		string Model,
		long Input,
		long Output,
		long CacheRead,
		long CacheWrite,
		long ReportedTokens,
		double Cost,
		double DurationMs,
		long Timestamp,
		long Messages);

	/// 只读取用量字段，content/details 直接 Skip。JsonDocument 会为整段正文建元数据，
	/// 这里用 Utf8JsonReader 避免为不用的内容分配。
	private static bool TryReadUsageLine(ReadOnlyMemory<byte> line, out UsageLineSample sample)
	{
		sample = default;
		if (line.IsEmpty)
		{
			return false;
		}

		try
		{
			var reader = new Utf8JsonReader(line.Span, isFinalBlock: true, state: default);
			if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
			{
				return false;
			}

			string? type = null;
			string? provider = null;
			string? model = null;
			var message = new MessageFields();
			var rootUsage = default(UsageValues);
			var hasRootUsage = false;

			while (reader.Read())
			{
				if (reader.TokenType == JsonTokenType.EndObject)
				{
					break;
				}

				if (reader.TokenType != JsonTokenType.PropertyName)
				{
					continue;
				}

				if (reader.ValueTextEquals("type"u8))
				{
					ReadString(ref reader, out type);
				}
				else if (reader.ValueTextEquals("provider"u8))
				{
					ReadString(ref reader, out provider);
				}
				else if (reader.ValueTextEquals("model"u8))
				{
					ReadString(ref reader, out model);
				}
				else if (reader.ValueTextEquals("message"u8))
				{
					ReadMessage(ref reader, ref message);
				}
				else if (reader.ValueTextEquals("usage"u8))
				{
					if (ReadUsage(ref reader, out var usage))
					{
						rootUsage = usage;
						hasRootUsage = true;
					}
				}
				else
				{
					SkipValue(ref reader);
				}
			}

			var isMessage = type is null || string.Equals(type, "message", StringComparison.Ordinal);
			if (isMessage)
			{
				if (message.Role is { Length: > 0 } && !string.Equals(message.Role, "assistant", StringComparison.Ordinal))
				{
					return false;
				}

				if (!message.HasUsage)
				{
					return false;
				}

				sample = new UsageLineSample(
					message.Provider ?? string.Empty,
					message.Model ?? string.Empty,
					message.Usage.Input,
					message.Usage.Output,
					message.Usage.CacheRead,
					message.Usage.CacheWrite,
					message.Usage.ReportedTokens,
					message.Usage.Cost,
					message.DurationMs,
					message.Timestamp,
					type is not null ? 1 : 0);
				return true;
			}

			if (string.Equals(type, "usage", StringComparison.Ordinal) && hasRootUsage)
			{
				sample = new UsageLineSample(
					provider ?? string.Empty,
					model ?? string.Empty,
					rootUsage.Input,
					rootUsage.Output,
					rootUsage.CacheRead,
					rootUsage.CacheWrite,
					rootUsage.ReportedTokens,
					rootUsage.Cost,
					0,
					0,
					0);
				return true;
			}

			return false;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static void ReadMessage(ref Utf8JsonReader reader, ref MessageFields message)
	{
		if (!reader.Read())
		{
			return;
		}

		if (reader.TokenType != JsonTokenType.StartObject)
		{
			SkipCurrent(ref reader);
			return;
		}

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				return;
			}

			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				continue;
			}

			if (reader.ValueTextEquals("provider"u8))
			{
				ReadString(ref reader, out message.Provider);
			}
			else if (reader.ValueTextEquals("model"u8))
			{
				ReadString(ref reader, out message.Model);
			}
			else if (reader.ValueTextEquals("role"u8))
			{
				ReadString(ref reader, out message.Role);
			}
			else if (reader.ValueTextEquals("usage"u8))
			{
				if (ReadUsage(ref reader, out var usage))
				{
					message.Usage = usage;
					message.HasUsage = true;
				}
			}
			else if (reader.ValueTextEquals("durationMs"u8))
			{
				ReadDouble(ref reader, out message.DurationMs);
			}
			else if (reader.ValueTextEquals("timestamp"u8))
			{
				ReadLong(ref reader, out message.Timestamp);
			}
			else
			{
				SkipValue(ref reader);
			}
		}
	}

	private static bool ReadUsage(ref Utf8JsonReader reader, out UsageValues usage)
	{
		usage = default;
		if (!reader.Read())
		{
			return false;
		}

		if (reader.TokenType != JsonTokenType.StartObject)
		{
			SkipCurrent(ref reader);
			return false;
		}

		long input = 0;
		long output = 0;
		long cacheRead = 0;
		long cacheWrite = 0;
		long reportedTokens = 0;
		var cost = 0.0;

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				usage = new UsageValues(input, output, cacheRead, cacheWrite, reportedTokens, cost);
				return true;
			}

			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				continue;
			}

			if (reader.ValueTextEquals("input"u8))
			{
				ReadLong(ref reader, out input);
			}
			else if (reader.ValueTextEquals("output"u8))
			{
				ReadLong(ref reader, out output);
			}
			else if (reader.ValueTextEquals("cacheRead"u8))
			{
				ReadLong(ref reader, out cacheRead);
			}
			else if (reader.ValueTextEquals("cacheWrite"u8))
			{
				ReadLong(ref reader, out cacheWrite);
			}
			else if (reader.ValueTextEquals("totalTokens"u8))
			{
				ReadLong(ref reader, out reportedTokens);
			}
			else if (reader.ValueTextEquals("cost"u8))
			{
				ReadCost(ref reader, out cost);
			}
			else
			{
				SkipValue(ref reader);
			}
		}

		return false;
	}

	private static void ReadCost(ref Utf8JsonReader reader, out double total)
	{
		total = 0;
		if (!reader.Read())
		{
			return;
		}

		if (reader.TokenType != JsonTokenType.StartObject)
		{
			SkipCurrent(ref reader);
			return;
		}

		while (reader.Read())
		{
			if (reader.TokenType == JsonTokenType.EndObject)
			{
				return;
			}

			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				continue;
			}

			if (reader.ValueTextEquals("total"u8))
			{
				ReadDouble(ref reader, out total);
			}
			else
			{
				SkipValue(ref reader);
			}
		}
	}

	private static void ReadString(ref Utf8JsonReader reader, out string? value)
	{
		value = null;
		if (!reader.Read())
		{
			return;
		}

		if (reader.TokenType == JsonTokenType.String)
		{
			value = reader.GetString();
			return;
		}

		SkipCurrent(ref reader);
	}

	private static void ReadLong(ref Utf8JsonReader reader, out long value)
	{
		value = 0;
		if (!reader.Read())
		{
			return;
		}

		if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
		{
			value = Math.Max(0, number);
			return;
		}

		SkipCurrent(ref reader);
	}

	private static void ReadDouble(ref Utf8JsonReader reader, out double value)
	{
		value = 0;
		if (!reader.Read())
		{
			return;
		}

		if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) && number > 0)
		{
			value = number;
			return;
		}

		SkipCurrent(ref reader);
	}

	private static void SkipValue(ref Utf8JsonReader reader)
	{
		if (!reader.Read())
		{
			return;
		}

		SkipCurrent(ref reader);
	}

	private static void SkipCurrent(ref Utf8JsonReader reader)
	{
		if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
		{
			reader.Skip();
		}
	}
}
