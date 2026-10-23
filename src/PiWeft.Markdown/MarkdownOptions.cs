namespace PiWeft.Markdown;

/// <summary>Immutable parsing options; changing them reparses the document.</summary>
public sealed record MarkdownOptions
{
	public bool UsePipeTables { get; init; } = true;
	public bool UseTaskLists { get; init; } = true;
	public bool UseAutoLinks { get; init; } = true;
	public bool UseStrikethrough { get; init; } = true;
	public bool UseInserted { get; init; } = true;
	public bool UseMarked { get; init; } = true;
	public bool SoftBreakAsNewLine { get; init; }
}
