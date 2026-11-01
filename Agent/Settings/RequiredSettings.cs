using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Agent.Settings;

// The configuration binder sets no `required` member it found nothing for: it leaves null where the
// type says never, and a section missing from the settings used to start the agent anyway, to fail
// with a NullReferenceException on that member's first use, far from its name. Checked once, as the
// settings are bound, naming each missing one by its configuration path.
public static class RequiredSettings
{
    public static T Checked<T>(T settings) where T : class
    {
        var missing = Missing(settings, "").ToList();
        return missing.Count == 0
            ? settings
            : throw new InvalidOperationException(
                $"The agent's settings are missing {string.Join(", ", missing)}, which it cannot start without.");
    }

    private static IEnumerable<string> Missing(object settings, string prefix) =>
        settings.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .SelectMany(property => Missing(property, property.GetValue(settings), PathOf(prefix, property.Name)));

    // A section that is there is checked for what it requires in turn; a list's entries are bound or
    // absent as a whole, and a plain value is only its own.
    private static IEnumerable<string> Missing(PropertyInfo property, object? value, string path) =>
        value switch
        {
            null when property.IsDefined(typeof(RequiredMemberAttribute)) => [path],
            null or string or IEnumerable => [],
            _ when value.GetType().IsDefined(typeof(RequiredMemberAttribute)) => Missing(value, path),
            _ => []
        };

    private static string PathOf(string prefix, string name)
    {
        var key = char.ToLowerInvariant(name[0]) + name[1..];
        return prefix.Length == 0 ? key : $"{prefix}:{key}";
    }
}