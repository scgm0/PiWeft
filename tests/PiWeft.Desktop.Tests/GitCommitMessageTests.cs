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

		/// <summary>逐次回放的回复（正文/思考/结束原因）；用完後回落到 <see cref="Reply"/>。</summary>
		public Queue<(string Text, string Thinking, StopReason Reason)> Script { get; } = new();

		/// <summary>每次调用收到的选项，用来断言思考档位与输出预算。</summary>
		public List<StreamOptions> Options { get; } = [];

		public AssistantMessageEventStream Stream(Model model, Context context, StreamOptions options)
		{
			Context = context;
			Options.Add(options);
			var (text, thinking, reason) = Script.Count > 0 ? Script.Dequeue() : (Reply, string.Empty, StopReason.Stop);
			var content = new List<ContentBlock>();
			if (thinking.Length > 0)
			{
				content.Add(new ThinkingContent { Thinking = thinking });
			}

			if (text.Length > 0)
			{
				content.Add(TextContent.Of(text));
			}

			var stream = new AssistantMessageEventStream();
			var message = new AssistantMessage
			{
				Content = content,
				StopReason = reason,
				Provider = model.Provider,
				Model = model.Id,
			};
			stream.Push(new StartEvent { Partial = message });
			if (thinking.Length > 0)
			{
				stream.Push(new ThinkingDeltaEvent { Partial = message, ContentIndex = 0, Delta = thinking });
			}

			if (text.Length > 0)
			{
				stream.Push(new TextDeltaEvent { Partial = message, ContentIndex = content.Count - 1, Delta = text });
			}

			stream.Push(new DoneEvent { Partial = message, Reason = reason });
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
		// 默认模板对齐 PiDeck：Gitmoji 表 + 分点正文，{diff} 被真实 diff 替换。
		Assert.Contains("Gitmoji", prompt, StringComparison.Ordinal);
		Assert.Contains("✨ feat", prompt, StringComparison.Ordinal);
		Assert.DoesNotContain(GitCommitMessage.DiffPlaceholder, prompt, StringComparison.Ordinal);
		Assert.Contains("+hello", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GenerateAsyncUsesTheCustomPromptTemplate()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test", Name = "Summary" };
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);
		var provider = new FakeProvider();
		gateway.Register(provider);

		await GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.Off, "+hello", "自定义模板\n{diff}", _ => null, CancellationToken.None);

		var prompt = Assert.IsType<UserMessage>(provider.Context!.Messages.Single()).Content.Text;
		Assert.Equal("自定义模板\n+hello", prompt);
	}

	[Fact]
	public void BuildPromptAppendsTheDiffWhenTheTemplateForgetsThePlaceholder()
	{
		var prompt = GitCommitMessage.BuildPrompt("+hello", "只写摘要");

		Assert.StartsWith("只写摘要", prompt, StringComparison.Ordinal);
		Assert.Contains("+hello", prompt, StringComparison.Ordinal);
	}

	[Fact]
	public void DefaultTemplateIsShownAndBlankFallsBackToIt()
	{
		Assert.Equal(GitCommitMessage.DefaultPromptTemplate, GitCommitMessage.NormalizeTemplate("  "));
		Assert.True(GitCommitMessage.IsDefaultTemplate(GitCommitMessage.DefaultPromptTemplate));
		Assert.Contains("Gitmoji", GitCommitMessage.DefaultPromptTemplate, StringComparison.Ordinal);
	}

	[Fact]
	public async Task GenerateAsyncRejectsAnEmptyStagedDiff()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test" };
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);

		var error = await Assert.ThrowsAsync<GitSummaryException>(() => GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.Off, " ", _ => null, CancellationToken.None));

		Assert.Equal(GitSummaryFailure.EmptyDiff, error.Failure);
		Assert.Equal(Strings.GitStageBeforeGenerate, error.Message);
	}

	[Fact]
	public async Task ReasoningThatEatsTheWholeBudgetRetriesWithoutThinking()
	{
		// 真实形态：思考档 + 小输出预算 → 推理吃满额度，正文一个字都不回（finish_reason=length）。
		// 旧代码写死 512 token 时就是这个表现，用户只看到「模型没有返回提交摘要」。
		var model = new Model
		{
			Id = "summary",
			Provider = "test",
			Api = "test",
			Name = "Summary",
			Reasoning = true,
			MaxTokens = 131_072,
		};
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);
		var provider = new FakeProvider();
		provider.Script.Enqueue((string.Empty, "想了很久", StopReason.Length));
		provider.Script.Enqueue(("✨ feat(test): 补上重试", string.Empty, StopReason.Stop));
		gateway.Register(provider);

		var text = await GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.High, "+hello", _ => null, CancellationToken.None);

		Assert.Equal("✨ feat(test): 补上重试", text);
		Assert.Equal(2, provider.Options.Count);
		Assert.Equal(ThinkingLevels.High, provider.Options[0].ThinkingLevel);
		Assert.Equal(ThinkingLevels.Off, provider.Options[1].ThinkingLevel);
		// 思考那一轮要拿到推理的空间：预算远大于不思考时的上限。
		Assert.True(provider.Options[0].MaxTokens > 1024, $"思考档预算只有 {provider.Options[0].MaxTokens}");
		Assert.True(provider.Options[1].MaxTokens <= 1024);
	}

	[Fact]
	public async Task StillEmptyOutputExplainsItself()
	{
		var model = new Model { Id = "summary", Provider = "test", Api = "test", Reasoning = true, MaxTokens = 8192 };
		var registry = new ModelRegistry();
		registry.Add(model);
		var gateway = new ModelsGateway(registry);
		var provider = new FakeProvider();
		provider.Script.Enqueue((string.Empty, string.Empty, StopReason.Stop));
		provider.Script.Enqueue((string.Empty, string.Empty, StopReason.Stop));
		gateway.Register(provider);

		var error = await Assert.ThrowsAsync<GitSummaryException>(() => GitCommitMessage.GenerateAsync(
			gateway, registry, model, ThinkingLevels.Off, "+hello", _ => null, CancellationToken.None));

		Assert.Equal(Strings.GitSummaryEmpty, error.Message);
		// 不思考时不该白跑第二轮。
		Assert.Single(provider.Options);
	}

	[Fact]
	public void OutputBudgetKeepsThinkingRoundsCheapButSufficient()
	{
		var huge = new Model { Id = "x", Provider = "test", Api = "test", Reasoning = true, MaxTokens = 384_000 };
		var tiny = new Model { Id = "y", Provider = "test", Api = "test", Reasoning = true, MaxTokens = 2048 };

		// 思考轮：给够推理空间，但从不越过模型自己声明的上限，也不拿几十万 token 的预算去拖慢等待。
		Assert.True(GitCommitMessage.OutputBudget(huge, ThinkingLevels.High) <= 16_384);
		Assert.Equal(2048, GitCommitMessage.OutputBudget(tiny, ThinkingLevels.High));
		// 不思考轮：一条提交信息用不了多少。
		Assert.Equal(1024, GitCommitMessage.OutputBudget(huge, ThinkingLevels.Off));
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
