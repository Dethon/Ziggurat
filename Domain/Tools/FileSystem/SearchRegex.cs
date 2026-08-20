using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

// A caller-supplied search pattern is untrusted twice over: it may not compile, and it may
// backtrack catastrophically. Compiling it here gives every filesystem the same bounded matcher
// and the same envelope for a pattern that cannot compile.
public static class SearchRegex
{
    public static FsResult<Regex> Compile(string query, bool regex, TimeSpan matchTimeout)
    {
        try
        {
            return new FsResult<Regex>.Ok(
                new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase, matchTimeout));
        }
        catch (ArgumentException ex)
        {
            return new FsResult<Regex>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.InvalidArgument,
                Message = $"Invalid search pattern '{query}': {ex.Message}",
                Hint = "Fix the regex, or set regex=false to match a literal string."
            });
        }
    }

    public static ToolErrorResult TimedOut(string query) => new()
    {
        ErrorCode = ToolError.Codes.Timeout,
        Message = $"Search pattern '{query}' timed out while matching.",
        Hint = "Simplify the regex (avoid nested quantifiers), or set regex=false to match a literal string."
    };
}