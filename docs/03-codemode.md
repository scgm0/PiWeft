# codemode 与 Jint

内置 `codemode` 让模型写 JavaScript，在 Jint 5 沙箱里调用其它工具。默认不启用：`--tools +codemode` 或 `settings.defaultTools: ["+codemode"]` 选中；存在 MCP `codemode` exposure 工具时自动启用（`settings.codemode.autoEnable`）。

实现位于 `src/PiWeft.JavaScript` 与 `src/PiWeft.CodingAgent/CodeMode*`。Jint 固定 `5.0.0-preview.2322`，只由 `nuget.config` 的 `Jint-preview` 源提供；升级前先确认该预览包仍可还原。

## 脚本面

async 函数体，支持顶层 `await`/`return`；首行可写：

```js
// @options: {"max_output_tokens": 10000, "timeout_ms": 60000}
```

可见的只有 `tools.<name>(args)`、`ALL_TOOLS`、发现函数、`models.*`、`text()`/`image()`/`exit()`/`console.*`、`store()`/`load()`；没有 Node、文件、网络、定时器或模块加载。`eval`/`new Function` 可用，动态源码同受预算约束；宿主桥在 prelude 里收进闭包并删除。

- `await searchTools(query, { limit?, namespace? })`：与 pi 相同的分词 + Okapi BM25（k1=1.2、b=0.75），在全部嵌套工具上排序。
- `await describeTool(name)` 返回 TypeScript 声明；`await describeNamespace(name)` 返回 `{ name, aliases, description, instructions, tools }`。命名空间为 `mcp__<server>`，服务器原名作别名。
- `models.*`：`getModelsOfType`/`getAvailableOfType`/`getModelOfType`（剔除凭据 headers，available 按凭据过滤）；`classify`/`generateImages` 走会话网关（并发 4，宿主校验 context，凭据由会话解析），调用进详情并汇入 `usage`。
- `store()`/`load()`：成功才写 `codemode-store` 条目，`load()` 按分支从根重建；值有大小限制。

## exposure 与加载

`direct` 直接声明、`codemode` 只给脚本、`deferred` 等 `tool_search` 加载、`hidden` 丢弃；MCP 默认 `codemode` 并自动启用 codemode。`tool_search` 用 BM25 命中未声明工具，写入 `tool-load` 条目并立即声明（随分支恢复/fork 保留）。`Agent` 区分可执行集合 `Tools` 与声明集合 `DeclaredTools`。

codemode 描述按命名空间分组，`inlineBudget`（默认 3000 token）控制总量：

- `on`：给直接声明补「`Codemode: tools.x(args)` resolves to …」，不再重复列出。
- `only`：列出全部脚本可调用工具并隐藏直接声明。

系统提示词的 `mcp_servers` 章节列出未直接声明的服务器与到达方式（描述 250 字符、整节 4096 字符截断）。

## 执行管线

嵌套调用走会话完整管线（`PrepareArguments`、schema 校验、计划模式安全网、`before/afterToolCall` 钩子，运行中经 `OnUpdate` 推送快照）；子结果不进 LLM 上下文；结束时未完成调用标记 `cancelled`；工具 `OutputSchema` 声明后脚本拿 `structuredContent`。

`return` 参与 `text N/M` 编号，console 最后；输出超预算保留首尾并落盘 `pi-codemode-*.txt`（`details.fullOutputPath`，`0` 时只留截断提示）；`image()` 保存 `pi-codemode-*.png|jpg|gif|webp` 并标注路径。

## 沙箱

每次执行新建引擎，手动搭建与 `ForUntrustedCode` 相同的预算（语句 200 万、托管 256 MiB、时限、源码/AST 上限、栈守卫），关闭 interop 反射/写入口；不启用 `ForUntrustedCode` 本身，因为其不可变配置会永久禁 eval。默认无超时（只有 `@options.timeout_ms` 与 abort）；无挂起工具/定时任务时报「Promise 永远不会落定」；并发解释器上限 2。专用后台线程泵 `Engine.Tasks`，脚本代码与 await 续体始终同线程。这是线程级隔离，不是 OS 沙箱。

## 宿主脚本与 AOT

`models.json` 的虚拟模型动态路由使用 `ScriptFunction`（同一套 Jint 约束）：每次新建解释器，只收到 `request`/`ctx` 两个 JSON 值，支持 async/await，有超时/语句/内存/源码上限；没有 codemode 的 `tools`/`models`/`store` 桥。

- 未移植 prelude lockdown；命名空间只来自 MCP；顶层 `arguments` 与 pi 略有差异。
- `PiWeft.JavaScript` 关闭 AOT/trim 分析器，CLI 发布带 Jint 裁剪警告；执行路径已在 `tools/PiWeft.AotSmoke` 实跑。
- AOT 下脚本包装必须用 async **箭头**函数：`async function` + 直接 eval 会触发 Jint 泛型分派缺陷。
- 硬 CPU/内存/生命周期边界仍需受限进程或容器，并通过 RPC 调用工具。

## 验证

```sh
dotnet test tests/PiWeft.JavaScript.Tests
dotnet test tests/PiWeft.CodingAgent.Tests --filter FullyQualifiedName~CodeMode
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
```

Jint 微探针结论：热进程新建引擎约 6–13 µs，近似 codemode 流程 JIT 1.68 ms / AOT 0.89 ms，首次初始化约 0.1–0.3 s，适合懒加载；大循环、大 JSON 与高频 hook 应留给 C#。AOT 探针约 17 MiB 增量（独立探针，不是发布包精确值）。
