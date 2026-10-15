namespace PiWeft.CodingAgent.Compaction;

/// <summary>摘要提示词，逐字抄自 pi，保证摘要格式兼容。</summary>
public static class CompactionPrompts
{
	public const string SummarizationSystemPrompt = """
		You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.

		Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.
		""";

	public const string SummarizationPrompt = """
		The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

		Use this EXACT format:

		## Goal
		[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

		## Constraints & Preferences
		- [Any constraints, preferences, or requirements mentioned by user]
		- [Or "(none)" if none were mentioned]

		## Progress
		### Done
		- [x] [Completed tasks/changes]

		### In Progress
		- [ ] [Current work]

		### Blocked
		- [Issues preventing progress, if any]

		## Key Decisions
		- **[Decision]**: [Brief rationale]

		## Next Steps
		1. [Ordered list of what should happen next]

		## Critical Context
		- [Any data, examples, or references needed to continue]
		- [Or "(none)" if not applicable]

		Keep each section concise. Preserve exact file paths, function names, and error messages.
		""";

	public const string UpdateSummarizationInstructions = """
		Update the existing structured summary with new information. RULES:
		- PRESERVE all existing information from the previous summary
		- ADD new progress, decisions, and context from the new messages
		- UPDATE the Progress section: move items from "In Progress" to "Done" when completed
		- UPDATE "Next Steps" based on what was accomplished
		- PRESERVE exact file paths, function names, and error messages
		""";

	public const string UpdateSummarizationPrompt = """
		The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

		""" + UpdateSummarizationInstructions;

	public const string TurnPrefixSummarizationPrompt = """
		The messages above are earlier context from an ongoing conversation. Later messages are stored separately and do not need to be reconstructed.

		Create a structured summary of this earlier context that another LLM will use to continue the work. Focus on what is needed to understand the later messages.

		Preserve exact file paths, function names, and error messages.
		""";
}
