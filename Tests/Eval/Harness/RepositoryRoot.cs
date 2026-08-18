namespace Tests.Eval.Harness;

// Where the repository is, from wherever the test binary happens to live. Two callers need it —
// the dumps land under it and the shipped agent settings are read out of it — and a second copy
// of this walk would be a second answer to the same question.
public static class RepositoryRoot
{
    public static string Path { get; } =
        Ancestors(new DirectoryInfo(AppContext.BaseDirectory))
            .FirstOrDefault(directory =>
                File.Exists(System.IO.Path.Combine(directory.FullName, "Ziggurat.sln")))
            ?.FullName
        ?? AppContext.BaseDirectory;

    private static IEnumerable<DirectoryInfo> Ancestors(DirectoryInfo directory) =>
        directory.Parent is null ? [directory] : [directory, .. Ancestors(directory.Parent)];
}