using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Domain.Tools.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Sugary.Watches;

namespace Domain.Security;

// The writes a later turn acts on, as the agent sees them leave: a schedule.json in the schedules
// mount, a watch.json in the home's watches or Sugary's glucose watches (both at /watchers). What one makes its object do is read off the text the write
// leaves there — a create's content, an edit's edits laid over what the agent read just before, a
// move's file as it was read under its new name — so the agent signs what the turn wrote and never a
// description a server handed it to sign. An edit and a move do rest on what the server answered
// that read with; a server that lies there gets a delegation for its own words with the turn's edit
// on top, and a create gives it nothing to lie about.
public static class DelegatedWrites
{
    // Whether a write to this path can leave something a later turn acts on: a schedule's file (or
    // the directory a create or a move names it by), a watch's file. Asked before anything is read.
    public static bool Delegates(string filesystem, string path) => filesystem switch
    {
        ScheduleFileSystem.Name => SchedulePath.Parse(path).Kind is ScheduleNodeKind.ScheduleFile or ScheduleNodeKind.ScheduleDir,
        HaWatchFileSystem.Name or SugaryWatchFileSystem.Name => HaWatchPath.Parse(path).Kind == HaWatchKind.WatchFile,
        _ => false
    };

    // The same for a path as a session's registry resolved it: a union's members are each asked, since
    // the union's own name is none of theirs (/watchers joins the home's watches and Sugary's).
    public static bool Delegates(FileSystemResolution at) => at.Backend is UnionFileSystem union
        ? union.Members.Any(m => Delegates(m.Backend.FilesystemName, at.RelativePath))
        : Delegates(at.Backend.FilesystemName, at.RelativePath);

    // A built app's ask leaves nothing a later turn acts on, and is signed nothing that would: the app's
    // text would otherwise run later as a full turn of the viewer, with the persona, every tool and their
    // memory. An ask acts on the apps channel, which no other turn does (AppViewerCredential), so the
    // identity the agent verified for it says so wherever its calls go.
    public static bool MayBeLeftBy(IdentityClaims? writer) => writer?.ChannelId != AppChannels.Apps;

    // `writer` is the writing turn's context: its agent runs a watch it creates, its origin is where a
    // watch naming no delivery answers. A write with none delegates nothing.
    public static DelegatedObject? Of(
        string filesystem, string path, string text, string? existing, ConversationContext? writer, TimeZoneInfo zone) =>
        filesystem switch
        {
            ScheduleFileSystem.Name => ScheduleDelegation.OfWrite(path, text, zone),
            HaWatchFileSystem.Name when writer is not null => WatchDelegation.OfWrite(path, text, existing, writer.AgentId, writer.Origin),
            SugaryWatchFileSystem.Name when writer is not null =>
                GlucoseWatchDelegation.OfWrite(path, text, existing, writer.AgentId, writer.Origin),
            _ => null
        };

    // The file a create over an existing object keeps part of: a watch replaced by a file that names
    // no delivery keeps the watch's own. A schedule cannot be created over another.
    public static bool CreateReadsFirst(string filesystem, bool overwrite) =>
        overwrite && filesystem is HaWatchFileSystem.Name or SugaryWatchFileSystem.Name;

    // The file a read before a write answered, as the mount keeps it: the watch mounts number every line
    // they answer the model with, and a watch.json under numbers parses as nothing, so an edit or a
    // replace would describe no watch and its fires would act for nobody.
    public static string FileText(string filesystem, string read) =>
        filesystem is HaWatchFileSystem.Name or SugaryWatchFileSystem.Name ? HaReadResult.Unnumbered(read) : read;

    // The file a move carries to its new name: a schedule directory's schedule.json. Watches do not move.
    public static string? MovedFile(string filesystem, string sourcePath) =>
        filesystem is ScheduleFileSystem.Name ? $"{sourcePath.TrimEnd('/')}/{SchedulePath.ScheduleFileName}" : null;
}