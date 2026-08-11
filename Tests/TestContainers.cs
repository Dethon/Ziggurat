using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace Tests;

// Every container and network the suite starts, under one name in Docker Desktop.
//
// Docker Desktop groups by the compose project label, and Testcontainers writes none — so a run
// scattered its dozen containers through the flat list, mixed in with whatever the machine was
// already running, and telling a leaked test container from a real one meant reading image names.
// Labelling them all with one project name is enough for Docker Desktop to fold them into a single
// expandable group, and costs nothing at runtime: the label is metadata the daemon stores and
// nothing in the suite reads back.
//
// The service label is what names the row inside that group, so it is worth passing something
// readable; without it Docker Desktop falls back to the container's own random name.
internal static class TestContainers
{
    public const string ProjectName = "ziggurat_tests";
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";

    public static ContainerBuilder Container(string image, string? service = null) =>
        Label(new ContainerBuilder(image), service ?? ServiceNameOf(image));

    public static ContainerBuilder Container(IImage image, string? service = null) =>
        Label(new ContainerBuilder(image), service ?? ServiceNameOf(image.FullName));

    public static NetworkBuilder Network() =>
        new NetworkBuilder().WithLabel(ProjectLabel, ProjectName);

    // The same two labels for a container the suite starts by shelling out to `docker run`. It
    // needs them more than a Testcontainers one does: labels are inherited from the image, and the
    // images the fixtures reuse were built by the dev stack's compose project, which stamps its own
    // name onto them — so an unlabelled run appears in Docker Desktop under jackbot.
    public static IEnumerable<string> LabelArgs(string service) =>
        ["--label", $"{ProjectLabel}={ProjectName}", "--label", $"{ServiceLabel}={service}"];

    private static ContainerBuilder Label(ContainerBuilder builder, string service) =>
        builder.WithLabel(ProjectLabel, ProjectName).WithLabel(ServiceLabel, service);

    // "lscr.io/linuxserver/jackett:0.24.306" -> "jackett". Registry, path and tag say nothing about
    // which of the suite's services this container is.
    private static string ServiceNameOf(string image)
    {
        var withoutTag = image.Split('@')[0];
        var lastColon = withoutTag.LastIndexOf(':');
        var lastSlash = withoutTag.LastIndexOf('/');
        if (lastColon > lastSlash)
        {
            withoutTag = withoutTag[..lastColon];
        }

        return withoutTag[(withoutTag.LastIndexOf('/') + 1)..];
    }
}