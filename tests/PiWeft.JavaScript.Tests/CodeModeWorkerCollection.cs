using Xunit;

namespace PiWeft.JavaScript.Tests;

/// codemode 的 worker 名额是进程级静态状态；相关测试互斥运行并独占进程环境。
[CollectionDefinition("codemode-workers", DisableParallelization = true)]
public sealed class CodeModeWorkerCollection;
