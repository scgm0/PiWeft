# pi / PiDeck 兼容与差距

PiWeft 复用 pi 兼容核心，桌面端用 MewUI + NativeAOT，PiDeck 用 Electron + React。本文记录桌面功能、配置安全边界和仍然存在的差距；当前实现状态见 [状态](00-status.md)。

对照基准（只读参考）：

| 对象 | 版本/路径 | 用途 |
|---|---|---|
| pi | 本地只读检出，`v1.3.0+30`（`482c1381a`） | 核心协议、CLI、扩展与 codemode |
| PiDeck | 本地只读检出，0.9.0 | 桌面工作流、配置与视觉基准 |
| pi_agent_rust / pi-go | 本地同级仓库 | 子代理、IDE、记忆与工具增强参考，不是必备契约 |

## 桌面视觉与主题

配色来自 PiDeck 的 `src/renderer/src/styles/foundation.css`，集中在 `AppPalette`；尺寸与字号集中在 `AppTheme`。完整视觉契约见 [DESIGN.md](../DESIGN.md)。

| 维度 | 取值（深色 / 浅色） |
|---|---|
| 背景 | app `#121212` / `#ffffff`，侧栏 `#171717` / `#ffffff`，弱化底 `#262626` / `#f4f4f5` |
| 文本 | primary `#ecece7` / `#202124`，secondary `#b8b8b2` / `#4b5563` |
| 边框 | default `#2e2e2e` / `#dfdfdf` |
| 强调色 | 近白 `#fafafa` / 近黑 `#18181b` |
| 代码块 | 随深浅主题配色；工具输出使用 `#111418` 深底与 `#d1d5db` 浅字 |
| 字号 | 辅助 12、caption 13、control 14、正文/输入默认 16（可选 14/16/18/20/22）、title 17、brand 18 |
| 圆角 / 间距 | 4/6/8/10/12；4/8/12/16/24 |
| 控件高度 | 28/32/36 |

主题切换调用 `Application.SetTheme`，不重建整棵界面或会话：

1. 长期存在的容器与按钮使用 `AppStyles` 命名样式，由 setter 按主题解析颜色。
2. `TextBlock` 不支持 `StyleName`，通过 `Tag` 角色与 `ApplyTextColors` 刷新前景色。
3. 图标缓存包含颜色，`AppIcons.RefreshAll` 更新存活控件，弱引用避免持有旧消息。
4. 消息卡片、工具、思考标题和 diff 原位换色，保留 Markdown 实例、选区、展开状态与配置草稿；分支树和 Git 不因主题变化重新加载。

MewUI 0.22.1 全部来自 NuGet；Linux 使用 X11 + MewVG，Windows 使用 Win32 + Direct2D，macOS 使用 MacOS + MewVG。正文默认 HarmonyOS Sans SC Regular，代码与终端使用 Maple Mono NF CN Regular（Nerd Font）；Lucide SVG 内嵌，许可随包。HarmonyOS Sans 的镜像许可与官方发布说明存在差异，分发前需核实。

## 桌面功能对照

| 能力 | 已实现 | 尚未覆盖 |
|---|---|---|
| 项目与会话 | 按 cwd 分组、跨项目环境隔离、并行标签、置顶、命名、归档/恢复、最近项目 | 项目级置顶、标签拖拽/分屏、批量操作 |
| 搜索 | 元数据与后台有界正文搜索、会话内筛选 | 正则、命中高亮 |
| 消息 | Markdown、语法高亮、代码复制、图片与点击放大、思考折叠、用户/助手复制 | 代码行号、跨行块注释、更多语言 |
| 工具 | 折叠摘要、状态/耗时、参数/输出、带行号的统一 patch、并排 diff | 完整文件交互 |
| 历史操作 | 编辑重发、重试、fork、分支树、直接/摘要跳转、HTML 导出 | 多选批量操作、分支比较 |
| 输入与模型 | 斜杠命令、命令面板、可改快捷键、可用模型筛选、供应商分组/折叠、搜索/刷新、最近模型优先、中文思考级别 | 模型收藏 |
| 附件 | 路径引用、图片文件/原生字节、剪贴板图片粘贴、复制预览图片、仅图片发送 | 更多媒体类型、把回复整体复制为图片 |
| 用量 | 会话统计、上下文、费用、缓存与计时、手动压缩；余额/积分查询；跨会话统计与 26 周热力图 | 完整供应商订阅窗口、热力图下钻 |
| 设置 | 模型/供应商、API Key、Agent、MCP、JSON、作用域与草稿保护 | 桌面 OAuth、技能/提示词管理、商店 |
| MCP | 启动连接、状态、stdio 工具热刷新、设置面板手动重连 | 自动重连、跨项目运行时管理 |
| Git | 状态、diff、在编辑器中打开、暂存/取消暂存、提交、分支切换/新建、检查点创建/恢复 | 推送/拉取、历史图谱、冲突解决界面 |
| 文件 | 目录导航、Git 标记、内置文本编辑器与冲突检测保存、回答级修改汇总 | 多文件标签、语法高亮编辑、正则搜索 |
| 终端 | MiniPty 伪终端、交互式 shell、Shell 选择、scrollback、选择、SGR/滚动区域/字符集/备用屏、缩放重排、DECSCUSR/DECSCNM/DECALN/DECRQM、OSC 4/10/11/12、OSC 8/52、鼠标上报、bracketed paste、IME、Ctrl+C/V、PRIMARY、右键菜单 | 完整 VT100 边界行为、终端多标签、快照回放 |
| 主题与窗口 | 深浅/系统主题预览、侧栏收起与宽度恢复、尺寸与零/负位置恢复、单实例 | 自定义强调色、多显示器可见性/最大化恢复 |

内嵌浏览器、自动更新、托盘、通知等不在实现范围内（命令 Dock 是基于 MiniPty 的伪终端，但不是完整终端仿真器）。

## 桌面交互与约束

- 正文与输入区居中并限制阅读宽度；默认 1060 DIP，可在外观页选择 760/1060/1360。正文与输入字号默认 16，可选 14–22；修改立即应用、记住偏好，不替换输入框、不清空草稿或选区。
- 消息时间线使用可变高度虚拟化，只构建视口附近控件；离开视口释放正文、Markdown 和图片，再次浏览恢复工具/思考展开状态。分支树延迟到首次打开时构建。核心历史仍保存在内存，尚未实现 JSONL 分页。
- 侧栏使用 14 DIP 标题和固定时间列；标题超宽省略。侧栏可收起，宽度与状态写入 `desktop.json`，搜索快捷键自动展开。
- 顶部状态只显示供应商、模型、思考档和忙碌状态；用量摘要统一放在输入区下方。统计浮层默认「概览」，明细按快照增量更新；缺失计时显示 `-`。
- 设置采用系统/配置分区与「工作区 / 维护」分类，导航独立滚动。配置编辑器只在首次进入配置管理时构造，归档只在访问归档页时扫描。
- 折叠工具只创建摘要，首次展开才创建原文、图片和 diff；移出视口后释放内容，仅记住展开状态。长输出不会预先构造文本布局。执行过程用左侧计数 pill + 图标时间线展示工具/思考/中间回复，工具行显示状态、耗时与命令摘要；超过 8 条时先显示最近 8 条，顶部折叠「显示更早的 N 条步骤」；底部显示收起与总耗时。
- 会话搜索先即时匹配标题和首条消息摘要；正文扫描等待 220 ms，新输入与关闭窗口取消旧请求。每个候选文件最多读取 512 KiB，超出预算的正文不参与搜索。
- 流式期间只更新正文，结束后解析 Markdown；40 ms 合并刷新，复用未变化历史控件与折叠状态。向上浏览时立即停止追随底部，手动回到末尾会重新进入追随；跟随动画进行中遇到内容增长会继续指向末尾。「移动到最新」是消息区底部居中的悬浮圆钮，带发丝轮廓和轻阴影，只在向上浏览时自动出现，回到底部自动隐藏；点击使用平滑动画。
- 代码块不嵌套纵向视口：普通滚轮移动会话列表，横向触控板和 Shift+滚轮浏览代码。差异正文使用一份只读 `MultiLineTextBox` 与 `ITextClassifier`，可跨行拖选复制，行号不混入复制内容。
- 用户消息把技能命令展开的 `<skill>` 块折叠成 chip，正文、时间和操作分层展示；长文默认折叠 8 行。图片以紧凑缩略图排在气泡上方，点击仍打开全尺寸预览。
- 扩展注入的内部上下文（会话文件里 `custom_message` 且 `display: false`，如目标模式 / 计划模式的提示）仍参与模型上下文、也仍是一轮的边界，但和 PiDeck 一样不在时间线里占用户气泡；`display: true` 的扩展通知照常显示。
- 用户消息和草稿里的 `@"path"` / `@path` 通过文本投影和内联对象显示为行内文件 chip：图标 + `@短文件名`（目录带尾斜杠和文件夹图标），完整路径仍保留在文档中，悬浮在 chip 上时贴着 chip 显示完整路径（相对引用按会话目录补成绝对路径），历史消息里点击打开。草稿里方向键按整枚跳过，Backspace/Delete 删除整枚，撤销可恢复；复制、编辑和发送始终使用原始 `@"path"`。代码块、行内代码和 URL 里的 `@` 不参与识别。
- 同一套投影也把正文里的 `/skill:名称` 画成行内技能 chip（sparkles + 技能名，Thinking 紫，对齐 PiDeck）：悬浮提示技能文件位置（解析不到时回退 `/skill:名称`），技能 chip 不可点击打开；原文 `/skill:名称` 保留，草稿里同样是整枚原子。代码块、行内代码和 URL 里的 `/skill:` 保持纯文本。
- 文件可拖入整个会话区；文件和目录插入草稿引用，图片后台有界读取为附件，不自动发送。剪贴板图片走同一条 `AddImage` 路径。
- 命令面板每次打开只取一份命令快照，最多显示 12 项；模型选择独立使用虚拟化供应商树，搜索覆盖全部模型，不截断为前 12 项。
- 可用模型要求 API 已实现且能解析当前运行环境凭据；不执行命令型凭据、不发网络探测。`--list-models` 保留完整目录语义，RPC `get_available_models` 与桌面一致。
- 命令/模型/图片面板使用窗口级覆盖层，同一窗口只打开一个；遮罩不穿透对话，Esc 可关闭，切换标签或销毁视图会清理浮层。
- 用户/助手原文、Markdown、代码和工具输出支持部分选择与复制；用户消息、思考、工具原文和流式正文使用自适应高度只读文本控件。
- 跨项目会话使用自己的 cwd 环境，启动项目 MCP 不注入其它项目标签。运行时发送按钮变为中断，保留草稿并禁用模型切换及历史修改动作；发送键右侧的 Split Send 菜单可把草稿加入当前回合（steer）或排到下一轮（follow-up），附件随消息入队。
- Markdown 链接只放行 http/https；编辑器优先 `$VISUAL` / `$EDITOR`；HTML 导出无脚本且自包含。

### 历史思考耗时与只读浏览

桌面通过 `recordInitialThinkingLevel: false` 打开会话，不追加条目或修改 mtime；真正开始对话时再记录实际级别。标准 pi 会话没有独立思考计时字段，`durationMs` 是整次回复耗时，不能据此推测思考耗时。`firstTokenMs` / `streamingDurationMs` 的口径见 [性能与基准](02-performance.md)。

## Linux PRIMARY 与图片剪贴板

Linux 上选中的原文、Markdown、代码和工具输出，以及普通输入框文本，自动发布到 PRIMARY；鼠标中键在可点击位置插入 PRIMARY。普通 Ctrl+C/Ctrl+V 的 CLIPBOARD 不受影响，密码框不发布文本，中键粘贴不自动发送且可撤销。
当前 MewUI Linux 宿主是 X11/XWayland。PRIMARY 默认由进程内原生实现持有：`X11PrimarySelection` 用独立 Display 连接与专用事件线程直接实现 X11 selection owner 协议，只依赖 `libX11.so.6`。事件线程用 `poll` 等待 X 连接与 `eventfd`，空闲时不轮询；超过单个请求块的文本按 ICCCM INCR 分块传输。`XOpenDisplay` 或资源创建失败时按 `xclip`、`xsel` 顺序回退外部程序；没有 DISPLAY 时才使用 `wl-copy`/`wl-paste`。纯 Wayland 且无 XWayland 时只能走回退路径。

```sh
xvfb-run -a -s '-screen 0 1600x1000x24' python3 tools/desktop-primary-smoke.py
```

图片剪贴板按 Ctrl+V/Cmd+V 时，如果剪贴板里有图片就按附件处理；没有图片时按键原样交给框架做文本粘贴。输入框默认右键菜单的「粘贴」替换为同一条图片优先命令，因此纯图片剪贴板时菜单项可用，与快捷键一致；只有文本时仍回退到文本框的默认文本粘贴。图片预览浮层提供「复制图片」，把原始编码字节写回系统剪贴板。MewUI 的 `IClipboardService` 只有文本，桌面侧新增 `IClipboardImages`（`HasImage` / `ReadAsync` / `TrySetImageAsync`），测试注入假实现即可覆盖交互。

- Linux：`xclip` 读写 PNG/JPEG/WebP/GIF；写入后保留 xclip 进程作为 owner，`xsel` 不支持图片类型。
- Windows：读写原生剪贴板；读取优先 `PNG`，其次 `CF_DIBV5`/`CF_DIB`，也处理 `CF_HDROP`；写入同时放 `CF_DIB`、`CF_DIBV5` 与编码格式。
- macOS：`NSPasteboard` 的 `public.png`/`public.jpeg`；`public.tiff` 经 `NSBitmapImageRep` 转 PNG。

解码统一使用 MewUI 的 `ImageDecoders` / `ImageSource.CopyPixels`。Windows 的 DIB↔PNG 互转自带最小实现（`DibCodec`、`PngCodec`），都是纯托管代码并用框架解码器交叉验证。

## 配置管理与安全边界

| PiDeck 源码 | 关键行为 | 本端口落点 |
|---|---|---|
| `config/ModelsTab.tsx` | 供应商与模型编辑、复制、导入导出，保留高级字段 | `ConfigurationView.Forms.cs`、`ConfigurationModels.cs` |
| `config/AuthTab.tsx` | 认证供应商清单，API Key 与 OAuth 分开 | API Key 管理；OAuth 只展示类型，禁止被表单覆盖 |
| `config/McpTab.tsx` | 全局/项目作用域、enabled、stdio/HTTP；保存不启动服务器 | `ConfigurationView.Mcp.cs`；明确下次启动生效 |
| `main/config/piConfigFileStore.ts` | revision、未知字段保留、同目录原子替换、外部修改冲突 | `ConfigurationStore.cs` |
| `main/config/ConfigManager.ts` | 配置领域与视图分离；模型拉取与探测按 API 分流 | `ProviderProbe`、`RemoteModelCatalog` |

- 分类设置页：系统设置与配置管理分开；保留环境、会话用量、归档、快捷键、日志和关于。
- 自定义供应商：新增、编辑端点和协议、复制、删除、导入 JSON、脱敏导出。模型可设置 ID、名称、上下文、输出上限、推理及图片输入；字段补丁保留 `headers`、`cost`、`compat` 等未编辑数据。
- 认证：API Key 遮罩输入、保存、删除；保存后清空敏感输入；已有 OAuth 不允许被 API Key 覆盖。
- Agent 设置：默认供应商/模型/思考级别、自动压缩和自动重试。未修改的继承值不制造项目覆盖。
- MCP：全局/项目配置，stdio/HTTP 编辑、参数数组、启停和删除；保留 env/headers 等高级字段。
- 源文件：模型、认证、设置和 MCP JSON，可显式显示、编辑和修复损坏文件。配置草稿独立于主题刷新；切页、重载、关闭设置弹窗和窗口均保护未保存修改。
- 文件读写限制在所选 agent 目录和固定的项目 `.pi` 路径，认证不允许写到项目作用域。读取和写入最多 2 MiB；只接受对象根节点，校验关键字段类型、模型 ID 和 MCP 名称。
- 写入使用同目录临时文件、刷新到磁盘后原子替换；Unix 新配置仅用户可读写。桌面写入之间使用专属排他锁，并检测 pi 的 `.lock` 标记与内容 revision；不宣称与非协作编辑器拥有事务互斥。
- 导出移除 API Key、认证相关字段、全部 headers/env/OAuth 和 URL 的查询、userinfo；不会自动导出 auth.json。源文件可能含密钥，默认不显示；配置操作不向日志输出异常原文或配置内容。
- 模型目录/Agent 设置更新作用于新会话；已有会话不进行运行时替换。MCP 保存不自动重连，设置面板提供手动「重连 MCP」。

## 虚拟模型 route 与状态

`models.json` 的虚拟模型既支持静态目标，也支持动态 JS 路由：

```json
{
  "providers": {
    "router": {
      "models": [
        {
          "id": "auto",
          "name": "Auto",
          "virtual": {
            "thinkingLevels": ["off", "high"],
            "route": "const target = ctx.modelRegistry.find('p', request.reason === 'retry' ? 'b' : 'a'); return { model: target, thinkingLevel: 'low', state: { reason: request.reason } };",
            "timeoutMs": 5000
          }
        }
      ]
    }
  }
}
```

- `request` 含 `model`、`thinkingLevel`、`reason`（`user`/`continuation`/`retry`/`direct`）、`previous`、`failed`、`state` 与 `messages`。
- `ctx.modelRegistry` 提供 `find`、`findOfType`、`getModelsOfType`、`getAvailableOfType` 与 `models`；只能路由到物理模型。
- 返回 `{ model: { provider, id }, thinkingLevel?, state? }`；`route` 可以是源码字符串或 `{ "code": "...", "timeoutMs": 5000 }`。
- `state` 写成 `pi.virtual-model-state` 自定义条目（`{ provider, modelId, state }`），按会话分支恢复；`direct` 请求（压缩、分支摘要、连接探测）不读写状态。
- 路由失败、目标不是物理模型或脚本超时会让本次请求以错误结束，不会落到 `pi-virtual`。

## 剩余差距

稳定编号沿用历史评估（`PI-*` 为 pi 能力，`D-*` 为桌面工作台，`X-*` 为参照方向），只列仍未完成的事项。

| 编号 | 能力 | 状态与剩余差距 |
|---|---|---|
| PI-01 | JS/TS 扩展宿主 | 未开始；已有 Jint codemode，但没有扩展加载、注册、生命周期、动态能力、重载与清理 |
| PI-02 | 扩展 UI 协议 | 对话框与 fire-and-forget 方法（`setStatus`/`setWidget`/`setTitle`/`set_editor_text`）均已按 pi 顶层字段实现，RPC/控制台/桌面有对应落点；缺少进程内 JS 扩展宿主与自定义 renderer |
| PI-03 | Pi 包管理 | 已实现 settings `packages` 来源解析、npm/git/本地安装与 `pi install/list/remove/update`；未覆盖 npm 依赖冲突治理、包图库与资源筛选界面 |
| PI-04 | 完整终端 TUI | 仍为按行 REPL；缺少多行编辑、`$EDITOR`、剪贴板图片粘贴进 TUI、终端流式工具展示、补全、选择器、终端图片和完整快捷键 |
| PI-05 | codemode / tool_search | 已实现；余项是 pi prelude 的 lockdown（见 [codemode 与 Jint](03-codemode.md)） |
| PI-06 | MCP 高级运行时 | 已覆盖 OAuth 授权码 + PKCE、客户端凭据、`oauth` 对象、`callbackPort`/`clientName`/`authServerMetadataUrl`、401 刷新重放、HTTP 通知流与反向应答；未覆盖 `cimd`、完整 `callbackUrl` 路径、与 pi 共享 `mcp-auth.json` 令牌格式、桌面 MCP 授权入口 |
| PI-07 | 认证覆盖 | 已支持 Codex、OpenAI ChatGPT、GitHub Copilot、Anthropic、OpenRouter、xAI、Kimi、Meta、Radius OAuth 与全 Provider API key；余项是 AWS profile/ECS/IRSA 完整 ambient 链与部分企业登录细分 |
| PI-08 | 动态模型目录 | 已实现 pi.dev 目录刷新、离线缓存、账号级 OpenRouter/Radius/GitHub Copilot 目录；余项是 ETag/刷新节流与更细的账号能力同步 |
| PI-09 | 高级模型运行时 | 已实现虚拟模型（静态目标 + `models.json` 动态 JS route + 路由状态条目）、cache warming（含 `cache_warming_decision` 扩展回调）、模型级 `samplingParams`、Codex WebSocket、生图/分类网关；余项是扩展宿主动态注册 route、`websocket-cached` 连接复用、Codex zstd 与完整 continuation，以及生图/分类入口接线 |
| PI-10 | 资源配置与主题 | 资源发现、主题 JSON 解析、主题目录热重载与资源页已实现；余项是独立资源编辑界面与包资源筛选可视化 |
| PI-11 | 项目信任 | 已实现 `trust.json`、父目录查找、显式覆盖、默认 `ask`、项目资源门控与启动询问 |
| PI-12 | 行内 shell 与会话环境变量 | 已实现 `!`/`!!` 与 bash（Windows 上还有 powershell）的 `PI_*` 注入 |
| PI-13 | Windows powershell 工具 | 已实现（Windows） |
| PI-14 | CLI 键位绑定 | 未实现 `keybindings.json` 与 `/hotkeys`；桌面快捷键体系独立 |
| PI-15 | pi 专属斜杠命令 | `/copy`、`/import`、`/share`、`/bug`、`/llama`、`/settings` 已实现 |
| D-01 | 问询、待办、计划 | 已实现原生 todo、ask_question、计划模式；未覆盖独立后台 Ask 工作流 |
| D-02 | 文件工作台 | 已实现目录导航、文本编辑、修改汇总、统一/并排 diff；未覆盖多文件标签、草稿本、跨行块注释、内嵌浏览器 |
| D-03 | 内置终端 | 已实现 MiniPty 与主要 VT 行为；未覆盖 macOS/Windows 实机回归、完整 VT100 边界、多标签、快照回放 |
| D-04 | 完整 Git 工作流 | 已实现暂存/提交/分支/检查点；未覆盖推送/拉取、历史图谱、冲突解决界面 |
| D-05 | 文件检查点/回滚 | 已实现 `refs/pi-checkpoints/<id>` 快照与回滚；未覆盖快照与助手回合关联、清理后新增未跟踪文件、检查点分页与 diff 预览 |
| D-06 | 配置深度 | 已实现连接测试、远程模型拉取、价格表单；未覆盖供应商迁移、收藏/隐藏/排序、Azure/Vertex/Bedrock 专用目录接口、桌面 OAuth |
| D-07 | 用量系统 | 已实现余额/积分查询、跨会话统计与热力图；未覆盖订阅窗口/重置时间、火山 AK/SK 等专用解析、热力图下钻 |
| D-08 | 技能/插件生态 | 已实现资源发现与资源页；缺少商店、中文提示词库、包安装可视化入口和 PiDeck 独立宿主插件系统 |
| D-09 | 多后端/导入 | 缺少 DSH、ACP 外部 Agent 后端，以及 Codex/Claude 等原生会话转换导入 |
| D-10 | 多模态工作流 | 图片输入/预览已有；缺少视觉桥、独立生图入口、语音转写/视频生成工作流 |
| D-11 | 远程/自动化 | 缺少局域网 Web、实时推送、外网访问令牌/隧道、飞书和定时任务 |
| D-12 | 桌面集成 | 缺少托盘、系统通知/历史、自动更新、快捷任务浮窗、桌面宠物；代理、进程监控和诊断包也未完整对齐 |
| D-13 | 交互/长历史 | 缺少项目级置顶/排序、标签拖拽/分屏、完整消息编辑删除、Mermaid/KaTeX；JSONL 历史仍全量保存在内存 |
| X-01 | 原生子代理、并行/链式编排、worktree 隔离 | 高价值但涉及执行所有权、取消、凭据和补丁合并 |
| X-02 | ACP、IDE 集成 | 与 D-09 有关联；服务端协议与桌面接入外部后端应分别规划 |
| X-03 | LSP/AST/DAP | 语义检索、诊断、结构化编辑、调试；不是基础工具的直接兼容要求 |
| X-04 | 长期记忆/检索 | 跨会话存储、索引、embedding 和隐私；不是会话压缩的替代 |
| X-05 | 后台任务/PTY 服务监督 | 需要生命周期、日志预算、取消和孤儿进程治理 |
| X-06 | 技能安全审计、能力/路径约束 | 文件工具路径限制不等于 Shell/进程的 OS 沙箱 |
| X-07 | Web 搜索、浏览器/桌面自动化、增强编辑/媒体工具 | 按产品需求选择，可以通过 MCP 或宿主工具提供 |

## 验证

```sh
xvfb-run -a dotnet test PiWeft.slnx --no-restore -v minimal
dotnet publish src/PiWeft.Desktop/PiWeft.Desktop.csproj -c Release -r linux-x64 --no-restore -v minimal

xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-parity-smoke.sh
PI_CODING_AGENT_DIR=$(mktemp -d) xvfb-run -a \
  src/PiWeft.Desktop/bin/Release/net10.0/linux-x64/publish/piweft-desktop --theme-smoke --cwd /tmp
xvfb-run -a -s '-screen 0 1600x1000x24' bash tools/desktop-statistics-smoke.sh
```

截图脚本使用独立 fixture agent 目录，不读取真实密钥或会话。修改进程环境变量的测试必须加入 `process-environment` 串行集合。MewUI 命名样式按目标控件类型校验；包版 Key 枚举没有标点键，不能根据上游 main 源码假设 API 可用。Windows/macOS 原生 GUI、发布包以及 PiDeck 完整 e2e 仍需独立验证。
