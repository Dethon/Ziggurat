using Domain.DTOs.Household;
using Domain.Tools.Sugary.Vfs;

namespace Domain.Tools.Sugary.Watches;

// Everyone of the household but the reader, each under the folder /sugary/people shows them by: the
// display name as SugaryPath.FolderOf makes it, two people whose names make one folder numbered in the
// order they joined. The same rule SugaryFileSystem lists people/ by, so a watch names a person exactly
// as the log does; the two must not drift (SugaryPeopleFoldersTests pins them together).
public static class SugaryPeopleFolders
{
    public static IReadOnlyList<(string Folder, SugaryPerson Person)> Of(IReadOnlyList<SugaryPerson> people, string readerUserId)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        return people
            .Where(person => person.UserId != readerUserId)
            .Select(person => (Free(SugaryPath.FolderOf(person.DisplayName), taken), person))
            .ToList();
    }

    private static string Free(string folder, HashSet<string> taken)
    {
        var candidate = folder;
        for (var n = 2; !taken.Add(candidate); n++)
        {
            candidate = $"{folder}-{n}";
        }

        return candidate;
    }
}