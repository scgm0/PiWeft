using Xunit;

namespace PiWeft.Cli.Tests;

/// <summary>
/// 修改进程环境变量（<c>PI_CODING_AGENT_DIR</c>、<c>PI_OFFLINE</c>、provider key 等）的测试必须归到这里。
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> 让这个集合与其它集合串行执行：
/// 仅同名 collection 并不足以隔离，环境变量是进程级的。
/// </summary>
[CollectionDefinition("process-environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
