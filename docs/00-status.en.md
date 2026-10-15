# PiWeft Status

PiWeft ports the core of [pi](https://github.com/earendil-works/pi) to C# / .NET 10, with a NativeAOT CLI (`pi`) and a MewUI desktop workspace (`piweft-desktop`). Session files interoperate with pi and PiDeck; this is not a complete implementation of either product.

The maintained status and constraints are in [Chinese](00-status.md). Build, run and packaging instructions are in the [README](../README.md).

## Compatibility

- `pi --version` reports RPC contract version **1.2.0**, not the desktop build version.
- Keep pi environment variables, agent/project directories, configuration filenames, version 3 JSONL sessions, RPC fields and accepted compatibility flags.
- System prompts are runtime-only. CLI startup records thinking metadata; desktop history browsing leaves files unchanged.
- Credentials resolve per requested provider. Header timeouts do not cut off long response streams; automatic retries exclude failed attempts from projected context without deleting history.
- Ten provider APIs, built-in tools, skills, prompt templates, compaction, branch summaries and MCP are implemented.
- MCP supports stdio and streamable HTTP, pagination, resources and prompts. List-change notifications currently work only over stdio.
- `extension_ui_response` answers the native `ask_question` UI request; other extension UI methods are not emitted. JS extensions and codemode are not implemented; accepting their flags does not provide an extension host. Project trust (`trust.json`) is implemented and gates project-level settings, MCP, skills and prompt templates.

Set PiDeck's `customPiPath` to the published CLI or a wrapper invoking `dotnet .../pi.dll`. Real-pi RPC comparison and PiDeck handshake tests guard the supported contract; they do not establish complete PiDeck UI parity.

## Desktop

Multi-project sessions and tabs, search, branching, Markdown and syntax highlighting, model selection, attachments, balance queries, configuration management, session and cross-session usage statistics, themes, Git status/diff with staging, commit and branch operations, checkpoints, a single-command dock and HTML export are implemented.

Details and remaining limitations:

- [Desktop parity](02-desktop-parity.md)
- [Configuration and security boundaries](03-pideck-configuration-parity.md)
- [Statistics and performance](04-session-statistics-performance.md)

Tray icons, notifications, auto-update, crash reporting, embedded terminals/browsers, mermaid and KaTeX are excluded by design. Full subscription-window queries, desktop OAuth and several advanced workspace features remain unimplemented.

## Verification

Run `dotnet test PiWeft.slnx` and `dotnet run --project tools/PiWeft.SessionBench`. GUI tests can run under Xvfb. The real-binary parity test requires TypeScript `pi` on PATH and fails if it is absent.

NativeAOT smoke and publish entry points are listed in the Chinese status document. Linux has native/GUI validation paths; Windows/macOS still need real-machine regression checks. Mock-provider tests do not replace live provider testing.
