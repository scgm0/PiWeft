using PiWeft.Ai;
using PiWeft.Ai.Providers;
using PiWeft.Desktop;
using Xunit;

namespace PiWeft.Desktop.Tests;

/// <summary>AI 提交摘要的模型输出清洗：围栏、前缀、引号与长度都要收成可直接提交的一条消息。</summary>
public sealed class GitCommitMessageTests
{
	private sealed class FakeProvider : IApiProvider
	{
		public string Api => "test";

		public Context? Context { get; private set; }

		public string Reply { get; init; } = "feat: add git panel";

		public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions options)
		{
			Context = context;
			var stream = new AssistantMessageEventStream();
			var message = new AssistantMessage
			{
				Content = [TextContent.Of(Reply)],
				StopReason = StopReason.Stop,
				Provider = model.Provider,
				Model = model.Id,
			};
			stream.Push(new StartEvent { Partial = message });
			stream.Push(new TextDeltaEvent { Partial = message, ContentIndex = 0, Delta = Reply });
			stream.Push(new DoneEvent { Partial = message, Reason = StopReason.Stop });
			return stream;
		}
	}

	[Fact]
	public async Task GenerateAsyncStreamsTheModelReplyAndSendsTheDiffPrompt()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test", Name = "Summary" };
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);
		var provider = new FakeProvider();
		gateway.Register(provider);

		var text = await GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.Off, "diff --git a/a.txt b/a.txt\n+hello",
			_ => null, CancellationToken.None);

		Assert.Equal("feat: add git panel", text);
		Assert.NotNull(provider.Context);
		var prompt = Assert.IsType<UserMessage>(provider.Context!.Messages.Single()).Content.Text;
		Assert.Contains("<diff>", prompt, StringComparison.Ordinal);
		Assert.Contains("+hello", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GenerateAsyncRejectsAnEmptyStagedDiff()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test" };
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.Off, " ", _ => null, CancellationToken.None));

		Assert.Equal(Strings.GitStageBeforeGenerate, error.Message);
	}

	[Fact]
	public void CodeFenceAndPrefixAreRemoved()
	{
		var text = GitCommitMessage.Sanitize("```\n提交信息：feat: add git panel\n\n- keep the tree compact\n```");

		Assert.Equal("feat: add git panel\n\n- keep the tree compact", text);
	}

	[Fact]
	public void SurroundingQuotesAreRemoved()
	{
		Assert.Equal("fix: keep scroll position", GitCommitMessage.Sanitize("\"fix: keep scroll position\""));
	}

	[Fact]
	public void BlankLinesAreCollapsedAndTheMessageIsBounded()
	{
		var text = GitCommitMessage.Sanitize(string.Join('\n', Enumerable.Range(0, 30).Select(index => index % 3 == 0 ? string.Empty : $"line {index}")));

		Assert.DoesNotContain("\n\n\n", text);
		Assert.True(text.Split('\n').Length <= 12);
	}

	[Fact]
	public void EmptyOrWhitespaceOutputStaysEmpty()
	{
		Assert.Equal(string.Empty, GitCommitMessage.Sanitize(""));
		Assert.Equal(string.Empty, GitCommitMessage.Sanitize(" \n\t\n"));
	}
}
