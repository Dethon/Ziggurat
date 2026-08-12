using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Domain.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

namespace Mcp.Hosting;

// The one place any MCP server reads its configuration, so the four questions thirteen copies each
// answered for themselves have one answer each.
//
// Order is load-bearing: environment variables first, user secrets last, so a user secret outranks
// an environment variable. That is the reverse of the framework default and it is deliberate — see
// docs/adr/0005-user-secrets-outrank-environment-variables.md before touching it.
//
// The two sources are added to the builder the caller handed in, which is the host's own
// configuration. So the ordering applies to everything the host reads afterwards — Kestrel, logging,
// --urls — not only to TSettings. That is deliberate: it is what each of the thirteen copied blocks
// did before, and a server that read its settings one way and its host another would be the
// surprise.
//
// The secrets id comes off the entry assembly rather than a Program type, because from here
// typeof(Program) would resolve to this assembly. Five servers have no UserSecretsId and the source
// is simply absent for them, which is exactly what they do today.
public static class SettingsBinder
{
    public static TSettings BindSettings<TSettings>(this IConfigurationBuilder configBuilder)
        where TSettings : class =>
        configBuilder.BindSettings<TSettings>(
            Assembly.GetEntryAssembly()?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId);

    internal static TSettings BindSettings<TSettings>(
        this IConfigurationBuilder configBuilder, string? userSecretsId)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(configBuilder);

        configBuilder.AddJsonFile(RetentionSettings.FileName, optional: true);
        configBuilder.AddEnvironmentVariables();
        if (userSecretsId is not null)
        {
            configBuilder.AddUserSecrets(userSecretsId, reloadOnChange: false);
        }

        // Non-null even from completely empty configuration: the binder builds the instance and
        // leaves unbound members at their defaults. What a missing section really produces is a null
        // sub-record, which is what the walk below is for.
        var configuration = configBuilder.Build();
        var settings = configuration.Get<TSettings>()!;

        var missing = MissingRequiredMembers(settings, configuration, path: "").ToList();
        return missing.Count == 0
            ? settings
            : throw new InvalidOperationException(
                "Required configuration is missing: " + string.Join(", ", missing.Select(Describe)) + ".");
    }

    private static string Describe(string path) =>
        $"{path} (environment variable {EnvironmentVariableName(path)})";

    // Bots[0].BotToken lives in the environment as Bots__0__BotToken: an index or key is one more
    // path segment there, not bracket syntax.
    private static string EnvironmentVariableName(string path) =>
        path.Replace("[", "__", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Replace(".", "__", StringComparison.Ordinal);

    // Null only, never empty. Six shipped servers carry required members that ship as "" and are
    // filled from secrets — ServiceBus, Telegram, WebSearch, HomeAssistant, Idealista and Library —
    // and an empty optional key is how a feature is switched off; an empty-is-invalid rule would
    // refuse to start them.
    // A property with no setter is computed rather than configured: nothing binds into it, so
    // validating it says nothing about the deployment, and a section-typed one that returns a fresh
    // instance of its own type would walk forever — a StackOverflowException at startup, which no
    // catch block can turn into a message.
    private static IEnumerable<string> MissingRequiredMembers(
        object instance, IConfiguration section, string path) =>
        instance.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0 && property.SetMethod is not null)
            .SelectMany(property => Inspect(property, instance, section, path));

    private static IEnumerable<string> Inspect(
        PropertyInfo property, object instance, IConfiguration section, string path)
    {
        var memberPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
        var value = property.GetValue(instance);

        if (value is null)
        {
            return IsRequired(property) ? [memberPath] : [];
        }

        // A required value type has no null to reveal an absent key — it binds to the type's
        // default and would sail past the null walk. Presence is therefore asked of the
        // configuration itself, so an explicit default written in config stays legal.
        //
        // A value that differs from the type default came from an initializer, which is the settings
        // type saying "leave this out and take this value" — voice's six defaulted sub-records are
        // the shipped shape, and holding one to a key it declared optional would fail startup on
        // every deployment that never wrote the section.
        if (property.PropertyType.IsValueType
            && IsRequired(property)
            && !section.GetSection(property.Name).Exists()
            && value.Equals(Activator.CreateInstance(property.PropertyType)))
        {
            return [memberPath];
        }

        // Recurses into a nested section, which is what a missing configuration block produces.
        // A settings type is not required to live in the same assembly as its root — a server can
        // bind a shared Domain record straight into its own settings — so this asks what the type
        // looks like, not where it was declared.
        if (IsSection(property.PropertyType))
        {
            return MissingRequiredMembers(value, section.GetSection(property.Name), memberPath);
        }

        // Collection elements are read at startup too: Telegram materialises BotRegistry(settings.Bots)
        // and voice SatelliteRegistry(settings.Satellites) at registration time, so a required member
        // missing from one element must fail startup by its indexed name instead of surfacing as a
        // null deep inside the server.
        return Elements(value)
            .Where(element => element.Value is not null && IsSection(element.Value.GetType()))
            .SelectMany(element => MissingRequiredMembers(
                element.Value!,
                section.GetSection(property.Name).GetSection(element.Key),
                $"{memberPath}[{element.Key}]"));
    }

    private static IEnumerable<(string Key, object? Value)> Elements(object value) =>
        value switch
        {
            string => [],
            IDictionary dictionary => dictionary.Keys.Cast<object>()
                .Select(key => (key.ToString()!, dictionary[key])),
            IEnumerable enumerable => enumerable.Cast<object?>()
                .Select((element, index) => (index.ToString(), element)),
            _ => []
        };

    private static bool IsRequired(PropertyInfo property) =>
        property.IsDefined(typeof(RequiredMemberAttribute), inherit: false);

    // A section is any bindable type the framework didn't ship — a settings root or a Domain record
    // nested inside one, from any assembly. Excluding the BCL rather than requiring assembly
    // equality with TSettings is what lets a nested type live somewhere else, and a record struct
    // section is walked like a class one: being a value type says nothing about whether the members
    // inside are required.
    private static bool IsSection(Type type) =>
        (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum))
        && type != typeof(string)
        && !typeof(IEnumerable).IsAssignableFrom(type)
        && !IsFrameworkType(type);

    // The namespace must match a root exactly or sit under it. A bare prefix match would read
    // "SystemX" or "MicrosoftFoo" as the BCL and walk straight past an application settings record,
    // switching off the startup gate for it without a word.
    private static bool IsFrameworkType(Type type) =>
        type.Assembly == typeof(object).Assembly
        || IsUnder(type.Namespace, "System")
        || IsUnder(type.Namespace, "Microsoft");

    private static bool IsUnder(string? nameSpace, string root) =>
        nameSpace == root || (nameSpace?.StartsWith(root + ".", StringComparison.Ordinal) ?? false);
}