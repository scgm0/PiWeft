using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.Agent;
using PiWeft.Ai;
using PiWeft.CodingAgent;
using PiWeft.CodingAgent.Session;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 上下文弹窗要能说明「占用是谁吃掉的」：系统提示词、工具 schema 与对话本身分开列，
/// 三段之和必须等于总占用（PiDeck 的 contextBreakdown 口径）。
/// </summary>
[Collection("mewui-controls")]
public sealed class ContextBreakdownTests
{
	private static AgentTool Tool(string name, string description, string schema = """{"type":"object","properties":{}}""") => new()
	{
		Name = name,
		Label = name,
		Description = description,
		Parameters = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(schema)!,
		Execute = static _ => Task.FromResult(AgentToolResult.Text("ok")),
	};

	[Fact]
	public void SystemAndToolDeclarationsAreCarvedOutAndTheRemainderIsConversation()
	{
		var system = new SystemMessage { Content = MessageContent.FromText(new string('x', 4_000)) };
		var user = new UserMessage { Content = MessageContent.FromText("hello") };
		var tools = new[] { Tool("read", "Read a file"), Tool("bash", "Run a command") };

		// 总占用 1M；系统提示约 1K，工具声明按 schema 估算，剩下全归对话。
		var breakdown = SessionStatisticsDisplay.ContextBreakdownOf([system, user], tools, 1_000_000);

		Assert.Equal(1_000, breakdown.SystemTokens);
		Assert.True(breakdown.ToolsTokens > 0, "工具 schema 也要占一格，否则读者会把它们算进对话");
		Assert.Equal(1_000_000, breakdown.Total);
	}

	[Fact]
	public void ConversationAbsorbsEverythingTheEstimatorCannotSplit()
	{
		// 没有系统消息与工具时，整段占用都是对话；差值不会漏掉 thinking / 图片这些不单列的内容。
		var breakdown = SessionStatisticsDisplay.ContextBreakdownOf([], [], 12_345);

		Assert.Equal(0, breakdown.SystemTokens);
		Assert.Equal(0, breakdown.ToolsTokens);
		Assert.Equal(12_345, breakdown.ConversationTokens);
	}

	[Fact]
	public void SystemAndToolsAreCappedByTheTotalSoSegmentsNeverOverflowTheBar()
	{
		// 畸形数据（估算值大于总占用）不能让三段之和超出总占用，否则进度条会画出窗口。
		var system = new SystemMessage { Content = MessageContent.FromText(new string('x', 40_000)) };
		var tools = new[] { Tool("read", new string('d', 40_000)) };

		var breakdown = SessionStatisticsDisplay.ContextBreakdownOf([system], tools, 100);

		Assert.Equal(100, breakdown.Total);
		Assert.Equal(0, breakdown.ConversationTokens);
	}

	[Fact]
	public void ToolDeclarationTokensAreCachedPerToolSet()
	{
		var tools = new[] { Tool("read", "Read a file") };
		var first = SessionStatisticsDisplay.EstimateToolDeclarationTokens(tools);
		Assert.True(first > 0);
		Assert.Equal(first, SessionStatisticsDisplay.EstimateToolDeclarationTokens(tools));
		Assert.Equal(0, SessionStatisticsDisplay.EstimateToolDeclarationTokens([]));
	}

	[Fact]
	public void StatisticsPanelShowsTheThreeSegmentsWithTheirShare()
	{
		var panel = new SessionStatisticsPanel(static () => { });
		var display = new SessionStatisticsDisplay(
			new SessionStatistics { LatestCacheHitPercent = 12, Input = 100, Output = 40 }, 128_000, 1_000_000, 3)
		{
			Breakdown = new ContextBreakdown(20_000, 8_000, 100_000),
		};
		panel.Update(display, false);
		panel.View.IsVisible = true;
		panel.View.Measure(new Size(720, 520));
		panel.View.Arrange(new Rect(0, 0, 720, 520));

		var texts = Find<TextBlock>(panel.View).Select(text => text.Text).ToArray();
		// 标题行给百分比与 ~已用/窗口；图例三段各带近似 token。
		Assert.Contains(texts, text => text.Contains("12.8%", StringComparison.Ordinal));
		Assert.Contains(texts, text => text.Contains("128K", StringComparison.Ordinal) && text.Contains("1M", StringComparison.Ordinal));
		Assert.Contains("系统", texts);
		Assert.Contains("工具", texts);
		Assert.Contains("对话", texts);
		Assert.Contains("~20K", texts);
		Assert.Contains("~8K", texts);
		Assert.Contains("~100K", texts);
		UiLifetime.Release(panel.View);
	}

	[Fact]
	public void MissingBreakdownDoesNotDrawAnEmptyBar()
	{
		var panel = new SessionStatisticsPanel(static () => { });
		panel.Update(new SessionStatisticsDisplay(new SessionStatistics(), 0, 1_000_000, 1), false);
		panel.View.IsVisible = true;
		panel.View.Measure(new Size(720, 520));
		panel.View.Arrange(new Rect(0, 0, 720, 520));

		// 没有数据时三段图例整块不占位：不会出现只有名字没有数字的空行。
		foreach (var label in new[] { "系统", "工具", "对话" })
		{
			Assert.DoesNotContain(Find<TextBlock>(panel.View), text => text.Text == label && EffectivelyVisible(text));
		}

		UiLifetime.Release(panel.View);
	}

	/// <summary>IsVisible 是本地状态；这里的断言要看祖先链是否真的把它显示出来。</summary>
	private static bool EffectivelyVisible(Element element)
	{
		for (Element? node = element; node is not null; node = node.Parent)
		{
			if (node is UIElement { IsVisible: false }) return false;
		}

		return true;
	}
}
