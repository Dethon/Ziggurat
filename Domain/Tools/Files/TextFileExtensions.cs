namespace Domain.Tools.Files;

// What a filesystem server counts as text: the extensions a text disk root will read, create and
// edit. It lives here, once, because two servers over the same kind of disk must not disagree —
// a file readable on the sandbox and unreadable on somebody's laptop is a difference with no
// reason behind it that anyone could find.
//
// A server may still hand its backend a list of its own. The vault does, deliberately narrower,
// and the outpost's --ext replaces this wholesale for one machine. What has gone is the second
// copy of the default.
public static class TextFileExtensions
{
    // A fresh array per read, so the shared default cannot be mutated through a caller that only
    // meant to hold it. The property is read at startup, so the allocation costs nothing.
    //
    // The empty entry is deliberate: it is how an extensionless file — Dockerfile, Makefile,
    // LICENSE — stays readable.
    public static string[] Default =>
    [
        "",
        ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg",
        ".py", ".sh", ".bash", ".zsh",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".html", ".htm", ".css", ".scss", ".sass", ".less",
        ".csv", ".tsv", ".xml", ".sql",
        ".log", ".env", ".gitignore", ".gitattributes", ".dockerignore",
        ".c", ".h", ".cpp", ".hpp", ".cs", ".java", ".kt", ".go", ".rs", ".rb", ".php",
        ".lua", ".pl", ".swift", ".scala", ".clj",
        ".ipynb"
    ];
}