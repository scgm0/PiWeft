using Xunit;

namespace PiWeft.Desktop.Tests;

/// 登记表是静态的测试替身路径：这几个用例串行跑，避免互相改指向。
[CollectionDefinition("worktree-registry", DisableParallelization = true)]
public sealed class WorktreeRegistryCollection;
