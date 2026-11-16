namespace Domain.Prompts;

// The library's workings without the download assistant's persona (Sections/library_guide.md),
// served by mcp-library beside DownloaderPrompt for an agent that is not jack.
public static class LibraryGuide
{
    public const string Name = "library_guide";

    public const string Description = "How media is searched for, fetched and filed in the library, without the download assistant's persona";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Text = _document.Text;
}