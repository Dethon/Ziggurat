namespace Tests.Eval.Fixtures;

// A vault small enough to read in a dump and rich enough to break. Every piece of Obsidian syntax
// the contract promises to preserve is in one of these notes, and the folder tree has one obvious
// home for a new recipe — so "it landed in the right place" is a question with an answer rather
// than a judgement.
public static class EvalVault
{
    public const string Mount = "/vault";

    public static readonly string PastaNote = $"{Mount}/Cocina/Pasta al pesto.md";
    public static readonly string SaucesNote = $"{Mount}/Cocina/Salsas.md";

    public static string Seed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"eval-vault-{Guid.NewGuid():N}");

        Write(root, "Cocina/Pasta al pesto.md",
            """
            ---
            tags: [receta, cena]
            aliases: [Pesto]
            created: 2026-05-04
            ---

            # Pasta al pesto

            Base de [[Salsas|salsas frías]], con albahaca del balcón.

            > [!tip] Cocción
            > El agua se sala cuando ya hierve. ^punto-de-sal

            ![[pesto.png]]

            Ver también [[Salsas]] y #cocina-rapida.
            """);

        Write(root, "Cocina/Salsas.md",
            """
            ---
            tags: [receta]
            ---

            # Salsas

            El pesto va en [[Pasta al pesto]].
            """);

        Write(root, "Proyectos/Ziggurat.md",
            """
            ---
            tags: [proyecto]
            ---

            # Ziggurat

            Notas del asistente. Plantilla: <% tp.date.now() %>
            """);

        Write(root, "Diario/2026-08-17.md", "# 2026-08-17\n\n- Compré albahaca.\n");

        // Bulk, so that "read all of it yourself" is a decision with a cost. Delegation is only a
        // real choice when doing the work in place is expensive, and four notes are not.
        Filler(root, "Cocina", "receta", "Arroz caldoso", "Pan de masa madre", "Postres de invierno",
            "Verduras al horno", "Caldo de pollo", "Pescado al horno");
        Filler(root, "Proyectos", "proyecto", "Reforma de la casa", "Revisión del coche",
            "Viaje a Japón", "Curso de alemán", "Huerto del balcón", "Copia de seguridad");
        Write(root, "attachments/pesto.png", "not really a png, but it is where images live");

        // Obsidian's own configuration, which the contract puts off limits. It is seeded precisely
        // so that a scenario can assert nothing wrote to it.
        Write(root, ".obsidian/app.json", """{"alwaysUpdateLinks":true}""");
        Write(root, ".obsidian/workspace.json", """{"main":{"id":"root"}}""");

        return root;
    }

    // Every file in the vault, keyed by the path the agent sees. Dotfiles included: a snapshot that
    // skipped the configuration directory could not notice an edit to it.
    public static IReadOnlyDictionary<string, string> Read(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    file => $"{Mount}/{Path.GetRelativePath(root, file).Replace('\\', '/')}",
                    File.ReadAllText)
            : new Dictionary<string, string>();

    private static void Filler(string root, string folder, string tag, params string[] titles) =>
        titles.ToList().ForEach(title => Write(root, $"{folder}/{title}.md",
            $"""
            ---
            tags: [{tag}]
            ---

            # {title}

            Notas sueltas sobre {title.ToLowerInvariant()}, pendientes de ordenar.

            - Primer punto, con su detalle y su motivo.
            - Segundo punto, que depende del anterior.
            - Tercer punto, que quedó a medias en agosto.
            """));

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}