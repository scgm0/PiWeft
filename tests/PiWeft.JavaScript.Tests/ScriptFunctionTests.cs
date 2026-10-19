using System.Text.Json.Nodes;
using Xunit;

namespace PiWeft.JavaScript.Tests;

public sealed class ScriptFunctionTests
{
	[Fact]
	public async Task ReturnsTheScriptValueAsJson()
	{
		var result = await ScriptFunction.InvokeAsync(
			"return { value: request.a + context.b, nested: { ok: true } };",
			new JsonObject { ["a"] = 2 },
			new JsonObject { ["b"] = 3 });

		Assert.NotNull(result);
		Assert.Equal(5, result!["value"]!.GetValue<int>());
		Assert.True(result["nested"]!["ok"]!.GetValue<bool>());
	}

	[Fact]
	public async Task SupportsAsyncBodiesAndAwait()
	{
		var result = await ScriptFunction.InvokeAsync(
			"const value = await Promise.resolve(41); return value + 1;",
			null,
			null);

		Assert.Equal(42, result!.GetValue<int>());
	}

	[Fact]
	public async Task ExposesNoHostModules()
	{
		var result = await ScriptFunction.InvokeAsync(
			"return { require: typeof require, process: typeof process, fs: typeof fs };",
			null,
			null);

		Assert.Equal("undefined", result!["require"]!.GetValue<string>());
		Assert.Equal("undefined", result["process"]!.GetValue<string>());
		Assert.Equal("undefined", result["fs"]!.GetValue<string>());
	}

	[Fact]
	public async Task ScriptErrorsPropagate()
	{
		await Assert.ThrowsAnyAsync<Exception>(() =>
			ScriptFunction.InvokeAsync("throw new Error('route failed');", null, null));
	}
}
