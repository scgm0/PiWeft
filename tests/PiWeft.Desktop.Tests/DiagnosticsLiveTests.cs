using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using PiWeft.CodingAgent.Resources;
using Xunit;
using static PiWeft.Desktop.Tests.VisualTree;

namespace PiWeft.Desktop.Tests;

/// <summary>
/// 诊断分区的实时刷新：数字每秒跟着新快照变（真实 FPS 是「最近一秒」的帧数，
/// 不是帧间隔环形缓冲的中位数），面板关闭后脉冲退场；长帧落盘带绘制量归因。
/// </summary>
[Collection("mewui-controls")]
public sealed class DiagnosticsLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
	private static string AgentDir()
	{
		var dir = Path.Combine(Path.GetTempPath(), "piweft-diag-live-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, "desktop.json"), "{\"diagnostics\":true}");
		return dir;
	}

	[Fact]
	public async Task PulseRefreshesTheLiveRegionAndRetiresWithTheSection()
	{
		var agentDir = AgentDir();
		Diagnostics.SetEnabled(true, agentDir);
		try
		{
			Diagnostics.Count("live.counter", 7);
			// 喂两个帧样本：帧间隔磁贴只在有样本时显示「实时 … fps」一行。
			Diagnostics.ObserveFrame(default);
			Thread.Sleep(20);
			Diagnostics.ObserveFrame(default);
			var view = new SettingsView(() => new SettingsSnapshot
			{
				AgentDir = agentDir, Cwd = "/tmp", ModelCount = 0,
				Tools = [], Skills = [], McpServers = [], Resources = ResourceSnapshot.Empty,
			});
			// 脉冲靠视觉根判断「面板还在不在」：先挂窗再建分区，根缓存才是对的。
			using var window = new Window { Content = view.View };
			await view.SelectSectionAsync("diagnostics");
			view.View.Measure(new Size(1000, 700));
			view.View.Arrange(new Rect(0, 0, 1000, 700));

			// 每秒脉冲之后，新记的计数必须出现在实时区里。
			Diagnostics.Count("live.counter", 9);
			view.PulseDiagnostics();
			view.View.Measure(new Size(1000, 700));
			view.View.Arrange(new Rect(0, 0, 1000, 700));
			var texts = Find<TextBlock>(view.View).Select(block => block.Text).ToList();
			// MetricLine 的标签与数值是两个文本块，分别断言。
			Assert.Contains("live.counter", texts);
			Assert.Contains("×16", texts);
			Assert.Contains(texts, text => text.Contains("实时", StringComparison.Ordinal) && text.Contains("fps", StringComparison.Ordinal));

			// 切到别的分区：脉冲退场，再调也不该抛或复活旧面板。
			await view.SelectSectionAsync("appearance");
			view.PulseDiagnostics();
		}
		finally
		{
			Diagnostics.SetEnabled(false);
			Directory.Delete(agentDir, recursive: true);
		}
	}

	[Fact]
	public void LongFrameRecordCarriesDrawAttribution()
	{
		var agentDir = AgentDir();
		Diagnostics.SetEnabled(true, agentDir);
		try
		{
			// 两帧间隔 60ms：第二帧按长帧阈值（50ms）落一条 frame.long 明细。
			Diagnostics.ObserveFrame(default);
			Thread.Sleep(60);
			Diagnostics.ObserveFrame(default);
			var snapshot = Diagnostics.TakeSnapshot();
			var attribution = snapshot.Recent.FirstOrDefault(timing => timing.Name == "frame.long");
			Assert.NotNull(attribution);
			// 归因字段存在即可：发行包里框架相位计时不可用，明细记录的是绘制量与输入延迟。
			Assert.Contains("draws=", attribution.Detail, StringComparison.Ordinal);
		}
		finally
		{
			Diagnostics.SetEnabled(false);
			Directory.Delete(agentDir, recursive: true);
		}
	}
}
