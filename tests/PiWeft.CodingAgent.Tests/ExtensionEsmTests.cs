using System.Text.Json.Nodes;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent.Extensions;
using PiWeft.CodingAgent.Session;
using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// ESM 扩展路径：pi 生态的扩展是 `export default function (pi) {...}`，工具参数用 TypeBox。
/// 这些用例真的加载 `.mjs` 文件，覆盖宿主模块、相对 import 与异步处理器三条路线。
[Collection("process-environment")]
public sealed class ExtensionEsmTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "piweft-esm-" + Guid.NewGuid().ToString("N")[..8]);
	private readonly string _agentDir;
	private readonly string _cwd;

	public ExtensionEsmTests()
	{
		_agentDir = Path.Combine(_root, "agent");
		_cwd = Path.Combine(_root, "project");
		Directory.CreateDirectory(Path.Combine(_agentDir, ExtensionDescriptor.DirectoryName));
		Directory.CreateDirectory(_cwd);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
			// 临时目录清理失败不影响断言结论。
		}
	}

	private string Write(string name, string source)
	{
		var directory = Path.Combine(_agentDir, ExtensionDescriptor.DirectoryName);
		var path = Path.Combine(directory, name);
		File.WriteAllText(path, source);
		return path;
	}

	private ExtensionHost Load()
	{
		var host = new ExtensionHost();
		host.Load(_cwd, trusted: true, _agentDir);
		return host;
	}

	[Fact]
	public async Task EsmExtensionImportsTypeBoxAndRegistersAnAsyncTool()
	{
		Write("esm-tool.mjs", """
			import { Type } from "typebox";
			import { isToolCallEventType } from "@earendil-works/pi-coding-agent";

			export default function (pi) {
				pi.registerTool({
					name: "esm_tool",
					label: "ESM",
					description: "reads a path",
					parameters: Type.Object({
						path: Type.String({ description: "file" }),
						limit: Type.Optional(Type.Number({ minimum: 1 })),
					}),
					execute: async (args) => ({ text: "read " + args.path }),
				});
				pi.on("tool_call", (event) => {
					if (isToolCallEventType("esm_tool", event)) { return { note: "seen" }; }
				});
			}
			""");

		using var host = Load();
		var state = Assert.Single(host.States);
		Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics));

		var tool = Assert.Single(host.Tools);
		Assert.Equal("esm_tool", tool.Name);
		var parameters = tool.Parameters;
		Assert.Equal("object", parameters["type"]!.GetValue<string>());
		Assert.Equal("string", parameters["properties"]!["path"]!["type"]!.GetValue<string>());
		Assert.Equal("file", parameters["properties"]!["path"]!["description"]!.GetValue<string>());
		Assert.Equal(1, parameters["properties"]!["limit"]!["minimum"]!.GetValue<int>());
		Assert.Equal(["path"], parameters["required"]!.AsArray().Select(item => item!.GetValue<string>()));

		var result = await tool.Execute(new AgentToolInvocation
		{
			ToolCallId = "c1",
			ToolName = "esm_tool",
			Arguments = new JsonObject { ["path"] = "a.txt" },
		});
		Assert.False(result.IsError);
		Assert.Equal("read a.txt", string.Concat(result.Content.OfType<TextContent>().Select(text => text.Text)));

		var payload = host.Dispatch("tool_call", new JsonObject { ["toolName"] = "esm_tool" });
		Assert.Equal("seen", payload["note"]!.GetValue<string>());
	}

	[Fact]
	public void EsmExtensionResolvesRelativeImports()
	{
		Write("helper.mjs", """
			export function label() { return "from helper"; }
			""");
		Write("entry.mjs", """
			import { label } from "./helper.mjs";

			export default function (pi) {
				pi.registerFlag("label", { type: "string", default: label() });
			}
			""");

		using var host = Load();
		var state = Assert.Single(host.States, item => item.Descriptor.FilePath.EndsWith("entry.mjs", StringComparison.Ordinal));
		Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics));
		Assert.Equal("from helper", host.Flags["label"].Default);
	}

	[Fact]
	public void AsyncHandlerResultIsMergedAfterThePromiseSettles()
	{
		Write("async-handler.mjs", """
			export default function (pi) {
				pi.on("turn_start", async () => {
					await Promise.resolve();
					return { extra: "async" };
				});
			}
			""");

		using var host = Load();
		var state = Assert.Single(host.States);
		Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics));

		var payload = host.Dispatch("turn_start", new JsonObject { ["turn"] = 1 });
		Assert.Equal("async", payload["extra"]!.GetValue<string>());
	}

	[Fact]
	public void PackageDiscoverySkipsNodeModules()
	{
		var packageRoot = Path.Combine(_root, "pkg");
		Directory.CreateDirectory(Path.Combine(packageRoot, "node_modules", "dep"));
		File.WriteAllText(Path.Combine(packageRoot, "entry.mjs"), "export default function (pi) {}");
		File.WriteAllText(Path.Combine(packageRoot, "node_modules", "dep", "index.mjs"), "export default function (pi) {}");

		using var host = new ExtensionHost();
		host.Load(_cwd, trusted: true, _agentDir, explicitPaths: [packageRoot]);

		var state = Assert.Single(host.States);
		Assert.EndsWith("entry.mjs", state.Descriptor.FilePath);
		Assert.True(state.Loaded, string.Join(" / ", state.Diagnostics));
	}

	[Fact]
	public void EsmExtensionWithoutDefaultExportIsDiagnosed()
	{
		Write("no-default.mjs", """
			export const value = 1;
			""");

		using var host = Load();
		var state = Assert.Single(host.States);
		Assert.False(state.Loaded);
		Assert.Contains(state.Diagnostics, message => message.Contains("默认导出函数", StringComparison.Ordinal));
	}
}
