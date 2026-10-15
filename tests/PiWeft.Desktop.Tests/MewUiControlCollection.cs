using Xunit;

namespace PiWeft.Desktop.Tests;

// MewUI visual-tree traversal reuses a process-global stack and is not thread-safe.
[CollectionDefinition("mewui-controls", DisableParallelization = true)]
public sealed class MewUiControlCollection : ICollectionFixture<MewUiPlatformFixture>;

/// <summary>Layout tests use the same registered platform/backend as the desktop.</summary>
public sealed class MewUiPlatformFixture
{
	public MewUiPlatformFixture() => typeof(Program).GetMethod("RegisterPlatform",
		System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
}
