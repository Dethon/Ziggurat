using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Skills;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Skills;

// The reader a preload reads with is the turn's own file_read over the session's mounts, so the
// result it puts in the conversation is the one the tool would have given the model.
public class SkillPreloadReadsTests
{
    [Fact]
    public async Task ReaderOverARegistry_AnswersWhatTheReadToolAnswers()
    {
        var registry = new Mock<IVirtualFileSystemRegistry>();
        var backend = new Mock<IFileSystemBackend>();
        registry.Setup(r => r.Resolve("/ha/setup-index.md"))
            .Returns(new FsResult<FileSystemResolution>.Ok(new FileSystemResolution(backend.Object, "setup-index.md")));
        backend.Setup(b => b.ReadAsync("setup-index.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FsResult<FsReadResult>.Ok(new FsReadResult
            {
                FilePath = "setup-index.md", Content = "1: ## Current Home Assistant setup", TotalLines = 1, Truncated = false
            }));

        var reader = SkillPreloadReads.ReaderOver(registry.Object).ShouldNotBeNull();
        var result = await reader("/ha/setup-index.md", CancellationToken.None);

        result!["filePath"]!.GetValue<string>().ShouldBe("/ha/setup-index.md");
        result["content"]!.GetValue<string>().ShouldBe("1: ## Current Home Assistant setup");
    }

    [Fact]
    public void ReaderOverNoRegistry_IsNoReader()
    {
        SkillPreloadReads.ReaderOver(null).ShouldBeNull();
    }
}