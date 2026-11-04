using Xunit;

namespace PiWeft.Desktop.Tests;

// 原生桌面 smoke 会启动独立进程并驱动真实的窗口/主题/PRIMARY；和进程内的 MewUI 布局测试并行时，
// Application/窗口全局状态与 CPU 时序会互相干扰。整个集合与其它集合串行执行。
[CollectionDefinition("native-desktop", DisableParallelization = true)]
public sealed class NativeDesktopCollection;
