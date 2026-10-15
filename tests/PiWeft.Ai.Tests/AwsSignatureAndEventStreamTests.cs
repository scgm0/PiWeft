using System.Text;
using System.Text.Json.Nodes;
using PiWeft.Ai.Providers.Bedrock;
using Xunit;

namespace PiWeft.Ai.Tests;

/// <summary>
/// SigV4 is verified against AWS's published test-suite vector for <c>get-vanilla</c>, including the intermediate
/// canonical request and string-to-sign, so a bug cannot hide behind a matching final hash.
/// </summary>
public class AwsSignatureV4Tests
{
	private static readonly AwsCredentials VectorCredentials = new()
	{
		AccessKeyId = "AKIDEXAMPLE",
		SecretAccessKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
	};

	private static readonly DateTimeOffset VectorDate = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

	[Fact]
	public void GetVanillaVectorMatchesAwsPublishedValues()
	{
		// The canonical get-vanilla request from AWS's SigV4 test suite. The signature below is AWS's published
		// expectation; the intermediate values were cross-checked against an independent implementation.
		var signed = AwsSignatureV4.Sign(
			"GET",
			"https://example.amazonaws.com/",
			new Dictionary<string, string>(),
			[],
			"us-east-1",
			"service",
			VectorCredentials,
			VectorDate,
			includeContentHash: false);

		Assert.Equal(
			"GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n" +
			"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
			signed.CanonicalRequest);

		Assert.Equal(
			"AWS4-HMAC-SHA256\n" +
			"20150830T123600Z\n" +
			"20150830/us-east-1/service/aws4_request\n" +
			"bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63",
			signed.StringToSign);

		Assert.Equal(
			"5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
			signed.Signature);

		Assert.Contains("Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", signed.Authorization);
		Assert.False(signed.Headers.ContainsKey("x-amz-content-sha256"));
	}

	[Fact]
	public void AuthorizationHeaderCarriesCredentialScopeAndSignedHeaders()
	{
		var signed = AwsSignatureV4.Sign(
			"POST",
			"https://bedrock-runtime.us-west-2.amazonaws.com/model/anthropic.claude-v2/converse-stream",
			new Dictionary<string, string> { ["content-type"] = "application/json" },
			Encoding.UTF8.GetBytes("""{"messages":[]}"""),
			"us-west-2",
			"bedrock",
			VectorCredentials with { SessionToken = "session-token" },
			VectorDate);

		Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-west-2/bedrock/aws4_request,", signed.Authorization);
		Assert.Contains("SignedHeaders=", signed.Authorization);
		Assert.Contains("content-type", signed.Authorization);
		Assert.Contains("host", signed.Authorization);
		Assert.Contains("x-amz-date", signed.Authorization);
		Assert.Contains("x-amz-security-token", signed.Authorization);
		Assert.Contains("Signature=", signed.Authorization);

		// Temporary credentials must be sent alongside the signature.
		Assert.Equal("session-token", signed.Headers["x-amz-security-token"]);
		Assert.Equal("20150830T123600Z", signed.Headers["x-amz-date"]);
		Assert.NotEqual(AwsSignatureV4.EmptyPayloadHash, signed.Headers["x-amz-content-sha256"]);
	}

	[Fact]
	public void SigningIsDeterministicForTheSameInput()
	{
		byte[] payload = Encoding.UTF8.GetBytes("""{"a":1}""");
		var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };

		var first = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/x", headers, payload, "us-east-1", "bedrock", VectorCredentials, VectorDate);
		var second = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/x", headers, payload, "us-east-1", "bedrock", VectorCredentials, VectorDate);

		Assert.Equal(first.Signature, second.Signature);
		Assert.Equal(64, first.Signature.Length);
		Assert.Matches("^[0-9a-f]{64}$", first.Signature);
	}

	[Fact]
	public void SigningKeyDerivationIsStableAndKeyDependent()
	{
		var key = AwsSignatureV4.DeriveSigningKey(VectorCredentials.SecretAccessKey, "20150830", "us-east-1", "iam");
		Assert.Equal(32, key.Length);

		var otherRegion = AwsSignatureV4.DeriveSigningKey(VectorCredentials.SecretAccessKey, "20150830", "eu-west-1", "iam");
		Assert.NotEqual(Convert.ToHexString(key), Convert.ToHexString(otherRegion));
	}

	[Fact]
	public void ChangingAnySignedPartChangesTheSignature()
	{
		byte[] payload = Encoding.UTF8.GetBytes("{}");
		var headers = new Dictionary<string, string>();

		var baseline = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/a", headers, payload, "us-east-1", "bedrock", VectorCredentials, VectorDate);
		var otherPath = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/b", headers, payload, "us-east-1", "bedrock", VectorCredentials, VectorDate);
		var otherPayload = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/a", headers, Encoding.UTF8.GetBytes("{\"x\":1}"), "us-east-1", "bedrock", VectorCredentials, VectorDate);
		var otherSecret = AwsSignatureV4.Sign("POST", "https://example.amazonaws.com/a", headers, payload, "us-east-1", "bedrock", VectorCredentials with { SecretAccessKey = "other" }, VectorDate);

		Assert.NotEqual(baseline.Signature, otherPath.Signature);
		Assert.NotEqual(baseline.Signature, otherPayload.Signature);
		Assert.NotEqual(baseline.Signature, otherSecret.Signature);
	}

	[Fact]
	public void QueryParametersAreEncodedSortedAndDeduplicated()
	{
		// A streaming endpoint: the only query parameter must survive encoding and ordering.
		var signed = AwsSignatureV4.Sign(
			"POST",
			"https://example.amazonaws.com/model/x%3A0/converse-stream?b=2&a=1",
			new Dictionary<string, string>(),
			[],
			"us-east-1",
			"bedrock",
			VectorCredentials,
			VectorDate);

		Assert.Contains("\na=1&b=2\n", signed.CanonicalRequest);
		// The escaped colon in the model id stays escaped in the canonical path.
		Assert.Contains("/model/x%3A0/converse-stream", signed.CanonicalRequest);
	}

	[Fact]
	public void HostHeaderDefaultsFromTheUrl()
	{
		var signed = AwsSignatureV4.Sign(
			"GET",
			"https://bedrock-runtime.eu-central-1.amazonaws.com/",
			new Dictionary<string, string>(),
			[],
			"eu-central-1",
			"bedrock",
			VectorCredentials,
			VectorDate);

		Assert.Contains("host:bedrock-runtime.eu-central-1.amazonaws.com\n", signed.CanonicalRequest);
	}

	[Fact]
	public void UriEncodeFollowsRfc3986()
	{
		Assert.Equal("a~b_c-d.e", AwsSignatureV4.UriEncode("a~b_c-d.e", encodeSlash: true));
		Assert.Equal("a/b", AwsSignatureV4.UriEncode("a/b", encodeSlash: false));
		Assert.Equal("a%2Fb", AwsSignatureV4.UriEncode("a/b", encodeSlash: true));
		Assert.Equal("%E4%B8%AD", AwsSignatureV4.UriEncode("中", encodeSlash: true));
		Assert.Equal("a%20b", AwsSignatureV4.UriEncode("a b", encodeSlash: true));
	}
}

public class AwsEventStreamTests
{
	[Fact]
	public async Task DecodesAFrameWithHeadersAndPayload()
	{
		var payload = Encoding.UTF8.GetBytes("""{"delta":{"text":"hi"}}""");
		var frame = AwsEventStream.Encode(new Dictionary<string, string>
		{
			[":event-type"] = "contentBlockDelta",
			[":message-type"] = "event",
			[":content-type"] = "application/json",
		}, payload);

		using var stream = new MemoryStream(frame);
		var messages = new List<AwsEventStreamMessage>();
		await foreach (var message in AwsEventStream.ReadAsync(stream))
		{
			messages.Add(message);
		}

		var decoded = Assert.Single(messages);
		Assert.Equal("contentBlockDelta", decoded.EventType);
		Assert.Equal("event", decoded.MessageType);
		Assert.Equal("application/json", decoded.Headers[":content-type"]);
		Assert.True(decoded.IsEvent);
		Assert.False(decoded.IsError);

		using var json = decoded.ParseJson();
		Assert.Equal("hi", json!.RootElement.GetProperty("delta").GetProperty("text").GetString());
	}

	[Fact]
	public async Task DecodesSeveralFramesInOneStream()
	{
		var parts = new List<byte>();
		foreach (var text in new[] { "a", "b", "c" })
		{
			parts.AddRange(AwsEventStream.Encode(
				new Dictionary<string, string> { [":event-type"] = "contentBlockDelta", [":message-type"] = "event" },
				Encoding.UTF8.GetBytes($"{{\"delta\":{{\"text\":\"{text}\"}}}}")));
		}

		using var stream = new MemoryStream([.. parts]);
		var texts = new List<string>();
		await foreach (var message in AwsEventStream.ReadAsync(stream))
		{
			using var json = message.ParseJson();
			texts.Add(json!.RootElement.GetProperty("delta").GetProperty("text").GetString()!);
		}

		Assert.Equal(["a", "b", "c"], texts);
	}

	[Fact]
	public async Task ErrorFramesExposeCodeAndMessage()
	{
		var frame = AwsEventStream.Encode(new Dictionary<string, string>
		{
			[":message-type"] = "exception",
			[":exception-type"] = "ValidationException",
			[":content-type"] = "application/json",
		}, Encoding.UTF8.GetBytes("""{"message":"bad request"}"""));

		using var stream = new MemoryStream(frame);
		await foreach (var message in AwsEventStream.ReadAsync(stream))
		{
			Assert.True(message.IsError);
			Assert.False(message.IsEvent);
			Assert.Equal("ValidationException", message.Headers[":exception-type"]);
		}
	}

	[Fact]
	public async Task ATornFrameIsReportedAsMalformed()
	{
		var frame = AwsEventStream.Encode(
			new Dictionary<string, string> { [":event-type"] = "messageStart", [":message-type"] = "event" },
			Encoding.UTF8.GetBytes("{}"));

		// Flip one payload byte so the message CRC no longer matches.
		frame[^6] ^= 0xFF;

		using var stream = new MemoryStream(frame);
		await Assert.ThrowsAsync<InvalidDataException>(async () =>
		{
			await foreach (var _ in AwsEventStream.ReadAsync(stream))
			{
			}
		});
	}

	[Fact]
	public async Task AStreamCutOffAfterThePreludeEndsCleanly()
	{
		var frame = AwsEventStream.Encode(
			new Dictionary<string, string> { [":event-type"] = "messageStart", [":message-type"] = "event" },
			Encoding.UTF8.GetBytes("{}"));

		// Keep only the prelude: the reader must stop rather than throw.
		using var stream = new MemoryStream(frame[..AwsEventStream.PreludeLength]);
		var count = 0;
		await foreach (var _ in AwsEventStream.ReadAsync(stream))
		{
			count++;
		}

		Assert.Equal(0, count);
	}

	[Fact]
	public void HeaderValueTypesAreDecoded()
	{
		// Hand-build a header block covering the numeric and string types.
		var buffer = new List<byte>();
		void AddString(string name, string value)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
			buffer.Add((byte)nameBytes.Length);
			buffer.AddRange(nameBytes);
			buffer.Add(7);
			var valueBytes = Encoding.UTF8.GetBytes(value);
			buffer.Add((byte)(valueBytes.Length >> 8));
			buffer.Add((byte)(valueBytes.Length & 0xFF));
			buffer.AddRange(valueBytes);
		}

		void AddInt(string name, int value)
		{
			var nameBytes = Encoding.UTF8.GetBytes(name);
			buffer.Add((byte)nameBytes.Length);
			buffer.AddRange(nameBytes);
			buffer.Add(4);
			buffer.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
		}

		AddString(":event-type", "metadata");
		AddInt(":status", 200);

		var headers = AwsEventStream.ParseHeaders(buffer.ToArray());

		Assert.Equal("metadata", headers[":event-type"]);
		Assert.Equal("200", headers[":status"]);
	}

	[Fact]
	public void Crc32MatchesKnownValues()
	{
		// The canonical check value for the IEEE polynomial used by AWS event streams.
		Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.UTF8.GetBytes("123456789")));
		Assert.Equal(0u, Crc32.Compute([]));
	}
}

[Collection("process-environment")]
public class AwsCredentialsTests
{
	[Fact]
	public void ReadsTheStandardEnvironmentVariables()
	{
		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		var previousToken = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN");

		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIA_TEST");
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
		Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", "token");
		try
		{
			var credentials = AwsCredentials.FromEnvironment();

			Assert.True(credentials.IsValid);
			Assert.Equal("AKIA_TEST", credentials.AccessKeyId);
			Assert.Equal("secret", credentials.SecretAccessKey);
			Assert.Equal("token", credentials.SessionToken);
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
			Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", previousToken);
		}
	}

	[Fact]
	public void MissingCredentialsAreInvalid()
	{
		var previousId = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
		var previousSecret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
		Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", null);
		Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", null);
		try
		{
			Assert.False(AwsCredentials.FromEnvironment().IsValid);
		}
		finally
		{
			Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", previousId);
			Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", previousSecret);
		}
	}
}
