using Xunit;

namespace PiWeft.CodingAgent.Tests;

/// 进程容器（systemd scope / setsid 进程组 / Job）相关的测试必须归到这里。
/// 并发跑别的 shell 测试时，容器创建与清理会排队，超时那几条断言就随机失败；
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> 让这个集合与其它集合串行执行。
[CollectionDefinition("shell-process", DisableParallelization = true)]
public sealed class ShellProcessCollection;
