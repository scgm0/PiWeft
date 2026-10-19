# 配置管理与安全边界

状态：核心配置管理已落地；完整 PiDeck 功能对齐尚未完成。对照 `/home/scgm/TestBuild/PiDeck`（只读参考）源码。

## 对照依据

| PiDeck 源码 | 关键行为 | 本端口落点 |
|---|---|---|
| `src/renderer/src/config/ModelsTab.tsx` | 供应商与模型编辑、复制、导入导出，保留高级字段 | `ConfigurationView.Forms.cs`、`ConfigurationModels.cs` |
| `src/renderer/src/config/AuthTab.tsx` | 认证供应商清单，API Key 与 OAuth 分开 | API Key 管理；OAuth 只展示类型，禁止被表单覆盖 |
| `src/renderer/src/config/McpTab.tsx` | 全局/项目作用域、enabled、stdio/HTTP；保存不启动服务器 | `ConfigurationView.Mcp.cs`；明确下次启动生效 |
| `src/main/config/piConfigFileStore.ts` | revision、未知字段保留、同目录原子替换、外部修改冲突 | `ConfigurationStore.cs` |
| `src/main/config/ConfigManager.ts` | 配置领域与视图分离；模型拉取与探测按 API 分流 | 已分离文件/字段逻辑；网络拉取与探测已移植（`ProviderProbe`、`RemoteModelCatalog`） |
| MewUI `Core/ThemeManager.cs`、`Core/Palette.cs` | 原始强调色跨主题固定；枚举强调色按深浅重新解析 | 启动改用 `Accent.Gray`；运行期不再改写框架 seed（MewUI 禁止），应用自有样式即时更新，自定义主题的框架控件配色需重启 |

## 功能范围

- 分类设置页：系统设置与配置管理分开；保留环境、会话用量、归档、快捷键、日志和关于。
- 外观页可直接选择深色、浅色或跟随系统，保存后立即换肤；`DesktopTheme.Apply` 负责同变体强制广播，避免设置页只写偏好不生效。
- 自定义供应商：新增、编辑端点和协议、复制、删除、导入 JSON、脱敏导出。
- 模型：新增、编辑、删除；可设置 ID、名称、上下文、输出上限、推理及图片输入。
- 模型字段补丁保留 `headers`、`cost`、`compat` 等未编辑数据，不重新序列化成窄 DTO。
- 认证：API Key 遮罩输入、保存、删除；保存后清空敏感输入。已有 OAuth 不允许被 API Key 覆盖。
- Agent 设置：默认供应商/模型/思考级别、自动压缩和自动重试。未修改的继承值不制造项目覆盖。
- MCP：全局/项目配置，stdio/HTTP 编辑、参数数组、启停和删除；保留 env/headers 等高级字段。
- 源文件：模型、认证、设置和 MCP JSON，可显式显示、编辑和修复损坏文件。
- 配置草稿独立于主题刷新；切页、重载、关闭设置弹窗和窗口均保护未保存修改。
- 保存后重新加载未来会话使用的模型目录与设置；不替换正在运行的会话。

## 安全与行为边界

- 文件读写限制在所选 agent 目录和固定的项目 `.pi` 路径，认证不允许写到项目作用域。
- 配置读取和写入最多 2 MiB；只接受对象根节点，校验关键字段类型、模型 ID 和 MCP 名称。
- 写入使用同目录临时文件、刷新到磁盘后原子替换；Unix 新配置仅用户可读写。
- 桌面写入之间使用专属排他锁，并检测 pi 的 `.lock` 标记与内容 revision。**并非完整实现 proper-lockfile 协议**，不宣称与非协作编辑器拥有事务互斥；外部进程恰好在最后一次 revision 检查与 rename 之间写入仍有竞态。
- 导出移除 API Key、认证相关字段、全部 headers/env/OAuth 和 URL 的查询、userinfo；不会自动导出 auth.json。
- 源文件可能含密钥，默认不显示；配置操作不向日志输出异常原文或配置内容。
- 供应商身份目前不支持跨文件迁移：已有供应商名称在表单中只读。原始 JSON 修改由用户显式操作，不自动迁移认证或默认模型引用。
- 模型目录/Agent 设置更新作用于新会话；已有会话不进行运行时替换。MCP 保存不自动重连，设置面板提供手动「重连 MCP」。
- 图文按钮仍使用项目内嵌 Lucide；未增加依赖。

## 验证

```sh
xvfb-run -a dotnet test PiWeft.slnx --no-restore -v minimal
# 2026-10-19 复核：非桌面 835 项通过；Desktop 437 项通过（X11 PrimarySelection 偶发失败，单独运行稳定）。

dotnet publish src/PiWeft.Desktop/PiWeft.Desktop.csproj -c Release -r linux-x64 --no-restore -v minimal
# NativeAOT 发布通过，未产生编译或 trim/AOT 警告。

xvfb-run -a -s '-screen 0 1440x900x24' bash tools/desktop-parity-smoke.sh
# 独立 fixture agent 目录，深色 1280x820 / 浅色 1000x720 截图；不读取真实密钥。

PI_CODING_AGENT_DIR=$(mktemp -d) xvfb-run -a \
  src/PiWeft.Desktop/bin/Release/net10.0/linux-x64/publish/piweft-desktop --theme-smoke --cwd /tmp
# selection、picker、border、draft、caret 验证通过；本次约 3 ms。
```

未验证 Windows/macOS 原生窗口与发布包，也未调用真实供应商进行联网测试。

## 剩余差距

按交互价值与改动风险排序，下一步应继续实现而不是用不可用按钮占位：

1. 供应商/模型搜索、收藏、隐藏与排序。
2. 完整订阅窗口与更多用量模板、批量模型操作和完整供应商迁移。
3. OAuth 桌面登录、MCP 自动重连与跨项目运行时管理。
4. 技能/提示词文件管理与商店；JS 扩展运行和安全/信任配置需要先确定 C# 运行时支持边界。
5. 项目工作区细节、文件抽屉、标签拖拽/分屏、更多消息与代码交互。
6. 自定义主题、多显示器可见性和最大化状态恢复，Windows/macOS 回归。

内嵌终端/浏览器、自动更新、托盘等在 `00-status.md` 中明确排除，本文不改变这些既有决策。
