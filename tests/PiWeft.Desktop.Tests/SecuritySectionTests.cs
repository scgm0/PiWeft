using Aprillz.MewUI.Controls;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// 安全门的分区视图：展开档位后编辑器确实进入视觉树，动作矩阵与列表编辑器都能构建。
[Collection("mewui-controls")]
public sealed class SecuritySectionTests : IDisposable
{
	private readonly string _agentDir = Path.Combine(
		Path.GetTempPath(), "piweft-security-" + Guid.NewGuid().ToString("N"));

	public SecuritySectionTests() => Directory.CreateDirectory(_agentDir);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_agentDir, recursive: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// 临时目录清理失败不影响断言。
		}
	}

	[Fact]
	public void ExpandedLevelBuildsTheEditor()
	{
		var section = new SecuritySection(() => _agentDir, () => { });
		section.ExpandFirstLevelForTest();
		ConversationPerformanceTests.Layout(section.View, 760, 900);

		var texts = Find<TextBlock>(section.View).Select(static block => block.Text).ToHashSet(StringComparer.Ordinal);
		Assert.Contains(Strings.SecurityCollapse, texts);
		Assert.Contains(Strings.SecurityToolActions, texts);
		Assert.Contains(Strings.SecurityDenyBash, texts);
		Assert.Contains(Strings.SecurityPathPolicy, texts);
	}
}
