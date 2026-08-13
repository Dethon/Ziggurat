using Infrastructure.Clients;
using Shouldly;

namespace Tests.Integration.Infrastructure;

public class LocalFileSystemClientTests : IDisposable
{
    private readonly string _testDir;
    private readonly LocalFileSystemClient _client = new();

    public LocalFileSystemClientTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"fs-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        GC.SuppressFinalize(this);
    }

    private async Task BuildTempTree(string[] setupFiles)
    {
        foreach (var entry in setupFiles)
        {
            var full = Path.Combine(_testDir, entry);
            if (entry.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
            }
            else
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                await File.WriteAllTextAsync(full, "content");
            }
        }
    }

    public sealed record GlobScenario(
        string Name,
        string[] SetupFiles,
        string Pattern,
        Action<string[], string> AssertResult);

    public static IEnumerable<object[]> GlobScenarios => new[]
    {
        new object[]
        {
            new GlobScenario(
                "FileWildcard",
                ["movie.mkv", "movie.mp4", "readme.txt"],
                "*.mkv",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("movie.mkv");
                })
        },
        new object[]
        {
            new GlobScenario(
                "NoTrailingSlash_FilesAndDirsWithDirsMarked",
                ["movies/", "todo.md"],
                "*",
                (hits, _) =>
                {
                    hits.ShouldContain(h => h.EndsWith("todo.md") && !h.EndsWith("/"));
                    hits.ShouldContain(h => h.EndsWith("movies/"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "TrailingSlash_DirsOnly",
                ["movies/", "todo.md"],
                "*/",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("movies/");
                })
        },
        new object[]
        {
            new GlobScenario(
                "RecursiveDirsOnly_NestedDirsMarked",
                ["movies/action/film.mkv"],
                "**/",
                (hits, _) =>
                {
                    hits.ShouldContain(h => h.EndsWith("movies/"));
                    hits.ShouldContain(h => h.EndsWith("action/"));
                    hits.ShouldAllBe(h => h.EndsWith("/"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "ExcludesRootDirectory",
                ["sub/"],
                "**/",
                (hits, testDir) =>
                {
                    hits.ShouldNotContain(h => h.TrimEnd('/') == testDir);
                })
        },
        new object[]
        {
            new GlobScenario(
                "RecursiveFilePattern_NestedFiles",
                ["sub/deep/", "root.txt", "sub/deep/nested.txt"],
                "**/*.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(2);
                    hits.ShouldContain(h => h.EndsWith("root.txt"));
                    hits.ShouldContain(h => h.EndsWith("nested.txt"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "BraceExpansion_MatchesAnyAlternative",
                ["movie.mkv", "movie.mp4", "poster.png", "readme.txt"],
                "*.{mkv,mp4,png}",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(3);
                    hits.ShouldContain(h => h.EndsWith("movie.mkv"));
                    hits.ShouldContain(h => h.EndsWith("movie.mp4"));
                    hits.ShouldContain(h => h.EndsWith("poster.png"));
                    hits.ShouldNotContain(h => h.EndsWith("readme.txt"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "NoMatches_EmptyArray",
                ["file.txt"],
                "*.pdf",
                (hits, _) => hits.ShouldBeEmpty())
        },
        // The edges below are the ones most at risk when the matcher underneath changes, so they
        // pin what ships rather than what the descriptions promise.
        new object[]
        {
            new GlobScenario(
                "Dotfile_MatchesALeadingWildcard",
                [".hidden.txt", "visible.txt"],
                "*.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(2);
                    hits.ShouldContain(h => h.EndsWith(".hidden.txt"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "BaseLevelPattern_DoesNotReachNestedFiles",
                ["root.txt", "sub/nested.txt"],
                "*.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("root.txt");
                })
        },
        new object[]
        {
            new GlobScenario(
                "RecursiveSegment_MatchesZeroSegments",
                ["root.txt", "sub/root.txt"],
                "**/root.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(2);
                    hits.ShouldContain(h => h.EndsWith($"{Path.DirectorySeparatorChar}root.txt")
                                            && !h.Contains("sub"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "RecursiveSegment_MatchesManySegments",
                ["a/b/c/deep.txt"],
                "**/deep.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("deep.txt");
                })
        },
        new object[]
        {
            new GlobScenario(
                "BareRecursiveWildcard_MatchesEveryDepth",
                ["root.txt", "sub/nested.txt"],
                "**",
                (hits, _) =>
                {
                    hits.ShouldContain(h => h.EndsWith("root.txt"));
                    hits.ShouldContain(h => h.EndsWith("nested.txt"));
                    hits.ShouldContain(h => h.EndsWith("sub/"));
                })
        },
        // Accidental, not intended: every glob description promises `?` matches one character, and
        // on the disk roots it never has — the batch matcher treats it as a literal. Pinned as it
        // ships so the matcher swap is judged against reality rather than against the prose.
        new object[]
        {
            new GlobScenario(
                "SingleCharacterWildcard_IsALiteral",
                ["a1.txt", "a?.txt"],
                "a?.txt",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("a?.txt");
                })
        },
        // The disk matcher has always matched case-insensitively, which is what makes `*.jpg` find
        // a camera's `.JPG`. Pinned here because the shared matcher is case-sensitive by default.
        new object[]
        {
            new GlobScenario(
                "PatternCase_IsIgnored",
                ["movie.MKV"],
                "*.mkv",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(1);
                    hits[0].ShouldEndWith("movie.MKV");
                })
        },
        new object[]
        {
            new GlobScenario(
                "TrailingSlashOnARecursivePattern_MatchesNestedDirsOnly",
                ["movies/action/film.mkv", "todo.md"],
                "movies/**/",
                (hits, _) =>
                {
                    hits.ShouldAllBe(h => h.EndsWith("/"));
                    hits.ShouldContain(h => h.EndsWith("action/"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "BraceAlternationOverDirectories_MatchesEachAlternative",
                ["books/a.epub", "movies/b.mkv", "music/c.flac"],
                "{books,movies}/*",
                (hits, _) =>
                {
                    hits.Length.ShouldBe(2);
                    hits.ShouldContain(h => h.EndsWith("a.epub"));
                    hits.ShouldContain(h => h.EndsWith("b.mkv"));
                })
        },
        new object[]
        {
            new GlobScenario(
                "AbsolutePaths",
                ["file.txt"],
                "**/*",
                (hits, testDir) => hits.ShouldAllBe(h => h.StartsWith(testDir)))
        },
    };

    [Theory]
    [MemberData(nameof(GlobScenarios))]
    public async Task Glob_ReturnsExpectedMatches(GlobScenario scenario)
    {
        await BuildTempTree(scenario.SetupFiles);

        var hits = await _client.Glob(_testDir, scenario.Pattern);

        scenario.AssertResult(hits, _testDir);
    }

    public sealed record MoveToTrashScenario(
        string Name,
        string[] SetupFiles,
        string[] InputsToTrash,
        Action<string[], string[]> AssertResult);

    public static IEnumerable<object[]> MoveToTrashScenarios => new[]
    {
        new object[]
        {
            new MoveToTrashScenario(
                "ExistingFile_MovesToUserTrashFolder",
                ["to-trash.txt"],
                ["to-trash.txt"],
                (inputs, trashPaths) =>
                {
                    var trashDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        LocalFileSystemClient.TrashFolderName);
                    var trashPath = trashPaths[0];

                    File.Exists(inputs[0]).ShouldBeFalse();
                    File.Exists(trashPath).ShouldBeTrue();
                    trashPath.ShouldStartWith(trashDir);
                    trashPath.ShouldContain("to-trash.txt");
                    File.ReadAllText(trashPath).ShouldBe("content");

                    File.Delete(trashPath);
                })
        },
        new object[]
        {
            new MoveToTrashScenario(
                "Directory_MovesDirectoryToTrash",
                ["to-trash-dir/file.txt"],
                ["to-trash-dir/"],
                (inputs, trashPaths) =>
                {
                    var trashDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        LocalFileSystemClient.TrashFolderName);
                    var trashPath = trashPaths[0];
                    var dirPath = inputs[0].TrimEnd('/');

                    Directory.Exists(dirPath).ShouldBeFalse();
                    Directory.Exists(trashPath).ShouldBeTrue();
                    trashPath.ShouldStartWith(trashDir);
                    trashPath.ShouldContain("to-trash-dir");
                    File.Exists(Path.Combine(trashPath, "file.txt")).ShouldBeTrue();

                    Directory.Delete(trashPath, true);
                })
        },
        new object[]
        {
            new MoveToTrashScenario(
                "MultipleFiles_CreatesUniqueTrashPaths",
                ["same-name.txt", "subdir/same-name.txt"],
                ["same-name.txt", "subdir/same-name.txt"],
                (_, trashPaths) =>
                {
                    trashPaths[0].ShouldNotBe(trashPaths[1]);
                    File.Exists(trashPaths[0]).ShouldBeTrue();
                    File.Exists(trashPaths[1]).ShouldBeTrue();

                    File.Delete(trashPaths[0]);
                    File.Delete(trashPaths[1]);
                })
        },
    };

    [Theory]
    [MemberData(nameof(MoveToTrashScenarios))]
    public async Task MoveToTrash_DispatchesByInputKind(MoveToTrashScenario scenario)
    {
        await BuildTempTree(scenario.SetupFiles);

        var absoluteInputs = scenario.InputsToTrash
            .Select(i => Path.Combine(_testDir, i.TrimEnd('/')))
            .ToArray();

        var trashPaths = new List<string>();
        foreach (var input in absoluteInputs)
        {
            trashPaths.Add(await _client.MoveToTrash(input));
        }

        scenario.AssertResult(absoluteInputs, trashPaths.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remove_NonExistentTargetDoesNotThrow(bool isDirectory)
    {
        var nonExistentPath = Path.Combine(
            _testDir,
            isDirectory ? "does-not-exist" : "does-not-exist.txt");

        if (isDirectory)
        {
            await Should.NotThrowAsync(async () => await _client.RemoveDirectory(nonExistentPath));
        }
        else
        {
            await Should.NotThrowAsync(async () => await _client.RemoveFile(nonExistentPath));
        }
    }

    [Fact]
    public async Task Move_WithNestedDirectories_CreatesParentDirectories()
    {
        // Arrange
        var sourceFile = Path.Combine(_testDir, "source.txt");
        var destFile = Path.Combine(_testDir, "nested", "deep", "dest.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        // Act
        await _client.Move(sourceFile, destFile);

        // Assert
        File.Exists(destFile).ShouldBeTrue();
        File.Exists(sourceFile).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveDirectory_WithContents_RemovesRecursively()
    {
        // Arrange
        var dirToRemove = Path.Combine(_testDir, "to-remove");
        Directory.CreateDirectory(Path.Combine(dirToRemove, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dirToRemove, "file1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(dirToRemove, "nested", "file2.txt"), "content");

        // Act
        await _client.RemoveDirectory(dirToRemove);

        // Assert
        Directory.Exists(dirToRemove).ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeDirectory_WithFiles_ReturnsFilesByDirectory()
    {
        // Arrange
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "root.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub2.txt"), "content");

        // Act
        var result = await _client.DescribeDirectory(_testDir);

        // Assert
        result.ShouldContainKey(_testDir);
        result[_testDir].ShouldContain("root.txt");
        result.ShouldContainKey(subDir);
        result[subDir].ShouldContain("sub1.txt");
        result[subDir].ShouldContain("sub2.txt");
    }

    [Fact]
    public async Task DescribeDirectory_WithNonExistentPath_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist");

        // Act & Assert
        await Should.ThrowAsync<DirectoryNotFoundException>(async () =>
            await _client.DescribeDirectory(nonExistentPath));
    }

    [Fact]
    public async Task Move_WithNonExistentSource_ThrowsIOException()
    {
        // Arrange
        var nonExistentSource = Path.Combine(_testDir, "does-not-exist.txt");
        var dest = Path.Combine(_testDir, "dest.txt");

        // Act & Assert
        await Should.ThrowAsync<IOException>(async () => await _client.Move(nonExistentSource, dest));
    }

    [Fact]
    public async Task Move_WithDirectory_MovesEntireDirectory()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDir, "source-dir");
        var destDir = Path.Combine(_testDir, "dest-dir");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file.txt"), "content");

        // Act
        await _client.Move(sourceDir, destDir);

        // Assert
        Directory.Exists(destDir).ShouldBeTrue();
        Directory.Exists(sourceDir).ShouldBeFalse();
        File.Exists(Path.Combine(destDir, "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveFile_WithExistingFile_RemovesFile()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "to-delete.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Act
        await _client.RemoveFile(filePath);

        // Assert
        File.Exists(filePath).ShouldBeFalse();
    }

    [Fact]
    public async Task MoveToTrash_WithNonExistentPath_ThrowsIOException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist.txt");

        // Act & Assert
        await Should.ThrowAsync<IOException>(async () =>
            await _client.MoveToTrash(nonExistentPath));
    }

    // The jail vets the argument path, but a symlink discovered inside the tree can point
    // anywhere — recursive walks must not follow it (or cycle on one), so glob skips symlinks.
    [Fact]
    public async Task Glob_SymlinksInsideTheTree_AreNotFollowedOrListed()
    {
        var outside = _testDir + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
            await BuildTempTree(["real.txt"]);
            Directory.CreateSymbolicLink(Path.Combine(_testDir, "linkdir"), outside);
            File.CreateSymbolicLink(Path.Combine(_testDir, "link.txt"), Path.Combine(outside, "secret.txt"));

            var hits = await _client.Glob(_testDir, "**/*");

            hits.ShouldContain(h => h.EndsWith("real.txt"));
            hits.ShouldNotContain(h => h.Contains("linkdir"));
            hits.ShouldNotContain(h => h.EndsWith("link.txt"));
            hits.ShouldNotContain(h => h.Contains("secret"));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    // Directory.Move to an existing destination throws IOException, which drives the same
    // recursive copy + delete fallback as an EXDEV cross-device move. The copy must not follow a
    // symlink out of the tree — it would exfiltrate the target's content into the destination.
    [Fact]
    public async Task Move_FallbackCopy_DoesNotFollowSymlinksOutOfTheTree()
    {
        var outside = _testDir + "-outside";
        Directory.CreateDirectory(outside);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
            await BuildTempTree(["src/real.txt"]);
            Directory.CreateSymbolicLink(Path.Combine(_testDir, "src", "linkdir"), outside);

            var destination = Path.Combine(_testDir, "dst");
            Directory.CreateDirectory(destination);

            await _client.Move(Path.Combine(_testDir, "src"), destination);

            File.Exists(Path.Combine(destination, "real.txt")).ShouldBeTrue();
            Directory.Exists(Path.Combine(destination, "linkdir")).ShouldBeFalse();
            File.Exists(Path.Combine(outside, "secret.txt")).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }
}