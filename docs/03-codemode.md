# codemode 与 Jint

内置 `codemode` 工具让模型写 JavaScript，在 Jint 5 沙箱里调用其它工具。默认不启用（与 pi 的 inactive 注册一致）：`--tools +codemode` 或 `settings.defaultTools: ["+codemode"]` 选中；存在 MCP `codemode` exposure 工具时按 pi 行为自动启用（`settings.codemode.autoEnable`）。

实现位于 `src/PiWeft.JavaScript` 与 `src/PiWeft.CodingAgent/CodeMode*`。Jint 固定在 `5.0.0-preview-2281`，只由 `nuget.config` 的 `Jint-preview` 源提供。

## 脚本面

async 函数体，支持顶层 `await`/`return`；首行可写：

```js
// @options: {"max_output_tokens": 10000, "timeout_ms": 60000}
```

可见的只有 `tools.<name>(args)`、`ALL_TOOLS`、发现函数、`models.*`、`text()`/`image()`/`exit()`/`console.*`、`store()`/`load()`；没有 Node、文件、网络、定时器或模块加载；`eval`/`new Function` 可用（与 pi 的 QuickJS 一致，动态源码同受预算约束）；宿主桥在 prelude 里收进闭包并删除。

- 发现：`searchTools(query, { limit?, namespace? })` 用与 pi 相同的分词 + Okapi BM25（k1=1.2、b=0.75）在全部嵌套工具上排序；`describeTool(name)` 返回 TypeScript 声明；`describeNamespace(name)` 返回 `{ name, aliases, description, instructions, tools }`。命名空间是 `mcp__<server>`，服务器原名作别名（`dev-radius` ≡ `dev_radius`），描述来自 `mcp.json`、instructions 来自服务器 initialize。
- `models.*`：目录查询 `getModelsOfType`/`getAvailableOfType`/`getModelOfType`（剔除凭据 headers，`getAvailableOfType` 按凭据过滤）；`classify(model, context)`/`generateImages(model, context)` 走会话的分类/生图网关（并发 4，context 形状宿主校验，凭据由会话解析），调用进详情并汇入 `usage`，未展示的生成图片会提示。
- `store()`/`load()`：`store()` 成功才写 `codemode-store` 条目，`load()` 按分支从根重建；值受大小限制。

## exposure 与加载

与 pi 一致：`direct` 直接声明、`codemode` 只给脚本、`deferred` 等 `tool_search` 加载、`hidden` 丢弃；MCP 默认 `codemode` 并自动启用 codemode。`tool_search` 用 BM25 加载未声明的 `codemode`/`deferred` 工具，命中写入 `tool-load` 条目并立即声明（随分支恢复/fork 保留）。`Agent` 区分可执行集合（`Tools`）与声明集合（`DeclaredTools`）。

codemode 描述按命名空间分组（`## mcp__docs` 附服务器描述；预算按组轮转，被裁掉的组标注 `(tools not listed)`/`(some tools not listed)`），`inlineBudget`（默认 3000 token）控制总量；`mode`：

- `on`：给直接声明补「`Codemode: tools.x(args)` resolves to …」并不再重复列出。
- `only`：列出全部脚本可调用工具并隐藏直接声明。

系统提示词的 `mcp_servers` 章节列出未直接声明的服务器与够到它们的方式（描述 250 字符、整节 4096 字符截断）。

## 执行管线

嵌套调用走会话完整管线（`PrepareArguments`、schema 校验、计划模式安全网、`before/afterToolCall` 钩子，运行中经 `OnUpdate` 推送快照）；子结果不进 LLM 上下文；结束时未完成的调用标记 `cancelled`；工具 `OutputSchema`（MCP 已映射）声明后脚本拿 `structuredContent`。

`return` 值参与 `text N/M` 编号、console 最后；输出超 `max_output_tokens` 保留首尾并落盘 `pi-codemode-*.txt`（details 给 `fullOutputPath`，`0` 时只保留截断提示）；`image()` 保存 `pi-codemode-*.png|jpg|gif|webp` 并标注路径。

## 沙箱与隔离

每次执行新建引擎，手动搭出与 `ForUntrustedCode` 相同的预算（语句 200 万、托管 256 MiB、时限、源码/AST 上限、栈守卫）并关闭 interop 反射/写入口——不启用 `ForUntrustedCode` 本身，因其不可变配置会永久禁 eval。默认无超时（只有 `@options.timeout_ms` 与 abort），无挂起工具/落定/定时任务时报「Promise 永远不会落定」；并发解释器上限 2（pi 的 `MAX_CONCURRENT_CODEMODE_WORKERS`）。专用后台线程泵 `Engine.Tasks`，脚本代码（含 await 续体）始终在同一线程；是线程级隔离，不是 OS 沙箱。等待事件驱动（取消注册唤醒，无轮询）；实测固定开销约 4 ms/执行、工具启动约 0.03 ms/个、重复 `searchTools()` 约 0.7 ms/百工具。

## Jint 探针结果

环境：Intel Core i5-9300H，Linux x64，.NET SDK 10.0.401 / Runtime 10.0.12，Release，Stopwatch 微探针。每项预热 20 次，采样 30–5000 次；没有做性能隔离，不是 BenchmarkDotNet 或完整应用测量。探针使用显式 `ClrFunction/JsObject/Tasks.RegisterPromise`，受限负载使用 `ForUntrustedCode`（200 万语句、64,000,000 bytes 分配、数组 100,000、入口/累计操作 10 秒、Promise 5 秒）。

| 工作负载 | JIT | NativeAOT |
|---|---:|---:|
| 进程内首次创建引擎、解析、求值 | 275.26 ms | 174.56 ms |
| 热进程新建空引擎并释放 | 13.00 us | 7.37 us |
| 热进程新建受限引擎并释放 | 13.35 us | 5.84 us |
| 新引擎解析并执行极小脚本 | 17.43 us | 6.28 us |
| 热引擎执行 prepared 极小脚本 | 0.82 us | 0.40 us |
| 简单受限扩展 hook | 4.03 us | 0.91 us |
| 1000 次显式同步宿主桥接 | 0.79 ms | 0.52 ms |
| 20 次立即完成的 Promise 桥接 | 0.147 ms | 0.065 ms |
| 20 个各等待 50 ms 的宿主调用并发 | 55.34 ms | 50.87 ms |
| 47,391 字符 / 1000 条 JSON 的受限解析筛选映射 | 2.74 ms | 1.17 ms |
| 每次新引擎的近似 codemode 流程 | 1.68 ms | 0.89 ms |
| 近似 codemode 流程 p95 | 2.71 ms | 1.33 ms |
| 10 万次 JS 数值循环，无受限配置 | 7.61 ms | 11.61 ms |
| 同一 JS 循环，受限配置 | 12.02 ms | 15.29 ms |
| 对应 C# 数值循环 | 0.057 ms | 0.050 ms |

近似 codemode 包含创建受限引擎、解析短 async 脚本、以 JSON 参数调用模拟宿主函数、通过 Promise 获取约 47 KB JSON、筛选 1000 条记录、序列化摘要和受限转换。不包含真实文件/网络/模型 I/O、完整许可和 Agent 调度。首次求值其他运行值为 JIT 290–324 ms、AOT 142 ms；冷路径不是稳定统计分位数。

内存与体积：保留 100 个执行过小型工具/JSON 负载的受限引擎，AOT 增加 7,861,880 bytes、JIT 7,881,408 bytes（约 77 KiB/引擎）；10 万次 JS 循环临时分配约 2.87 MB/次；JSON 筛选约 198 KB/次；近似 codemode AOT 临时分配约 181 KB/次。NativeAOT 探针可执行文件 19,160,720 bytes，空控制台 AOT 基线 1,270,840 bytes，差值约 17.06 MiB；这是独立探针，不是 PiWeft 发布包的精确增量。AOT 发布成功并运行全部探针，但有 Jint `IL2104`/`IL3053` 汇总警告。

结论：小型编排、轻量 hook、结果过滤通常是毫秒级成本，相对秒级 I/O 占比较小；首次使用约百毫秒初始化值得懒加载。大循环、大 JSON、高频 token hook 可能显著增加 CPU、复制和 GC，应将重计算留给 C#，合并/限频非必要事件。实际扩展数量、会话并发、上下文大小、工具数据量和引擎生命周期决定内存，不能用空引擎分配估计整个工作台。

## 宿主脚本

`models.json` 的虚拟模型动态路由使用 `ScriptFunction`（同一套 Jint 约束）：每次调用新建解释器，脚本只收到 `request`/`ctx` 两个 JSON 值，支持 async/`await`，超时、语句、内存与源码长度都有上限。它没有 codemode 的 `tools`/`models`/`store` 宿主桥，只做纯计算并返回路由结果；详见[pi / PiDeck 兼容](01-parity.md)的「虚拟模型 route 与状态」。

## 与 pi 的差异及 AOT

- 未移植 prelude 内建对象 lockdown（每次执行新建引擎，风险有限）；命名空间只来自 MCP 服务器（无扩展宿主）；顶层 `arguments` 因 AOT 箭头包装与 pi 略有差异。
- `PiWeft.JavaScript` 关闭 AOT/trim 分析器，CLI 发布带 Jint 裁剪警告。执行路径（eval/search/models）已在 `tools/PiWeft.AotSmoke` 实跑。
- AOT 下脚本包装必须用 async **箭头**函数：`async function` + 直接 eval 会触发 Jint 的泛型接口分派缺陷（`RhpCidResolve` FailFast）。
- 沙箱是线程级隔离，不是 OS 沙箱；硬 CPU/内存/生命周期边界仍需受限进程或容器，并以 RPC 调用工具。

## 验证

```sh
dotnet test tests/PiWeft.JavaScript.Tests
dotnet test tests/PiWeft.CodingAgent.Tests --filter FullyQualifiedName~CodeMode
dotnet publish tools/PiWeft.AotSmoke -c Release -r linux-x64
```

原始 Jint 探针位于 `/tmp/piweft-jint-eval-D9k8ZX/`（不保证长期存在）；正式试点前应把可复现探针迁入仓库工具，并在目标硬件上重测启动、首次调用、稳态、并发、RSS、分配、GC 与包体积。
