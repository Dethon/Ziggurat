using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class ThreadSessionToolFilterTests
{
    private static AITool Tool(string name) => AIFunctionFactory.Create(() => 0, name);

    // The order is the list the model reads, and a model that batches "load the skill and read
    // the index" takes an early tool that sounds right for the read. With the web tools leading,
    // claude-haiku-5.5 opened /ha/setup-index.md with web_browse or web_search in 16 of 66
    // replays of two real turns; with the filesystem tools leading, in 0 of 120 (2026-10-10).
    // Rewording the mount line and the browse tool's description moved nothing. Only the
    // filesystem tools move: with the subagent tool ahead of the web tools as well, gpt-6-luna's
    // throwaway first call landed on a worker.
    [Fact]
    public void Offered_TheFilesystemToolsLead_AndTheRestKeepTheirOrder()
    {
        AITool[] mcp = [Tool("mcp__mcp-websearch__web_search"), Tool("mcp__mcp-websearch__web_browse")];
        AITool[] domain = [Tool("domain__subagents__run_subagent"), Tool("domain__memory__memory_forget")];
        AITool[] filesystem = [Tool("domain__filesystem__file_read"), Tool("domain__filesystem__exec")];

        var offered = ThreadSessionBuilder.Offered(mcp, domain, filesystem);

        offered.Select(t => t.Name).ShouldBe(
        [
            "domain__filesystem__file_read", "domain__filesystem__exec",
            "mcp__mcp-websearch__web_search", "mcp__mcp-websearch__web_browse",
            "domain__subagents__run_subagent", "domain__memory__memory_forget"
        ]);
    }

    [Fact]
    public void FilterMcpTools_ChannelReceiveTool_IsAlwaysRemoved()
    {
        AITool[] tools =
        [
            Tool($"mcp__mcp-scheduling__{ChannelProtocol.ReceiveTool}"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-websearch__web_browse"]);
    }

    [Fact]
    public void FilterMcpTools_ChannelProtocolTools_AreAlwaysRemoved()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-scheduling__send_reply"),
            Tool("mcp__mcp-scheduling__request_approval"),
            Tool("mcp__mcp-scheduling__register_agents"),
            Tool("mcp__mcp-scheduling__create_conversation"),
            Tool("mcp__mcp-scheduling__fs_glob")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-scheduling__fs_glob"]);
    }

    [Fact]
    public void FilterMcpTools_FilesystemActive_RemovesRawFsTools()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-vault__fs_read"),
            Tool("mcp__mcp-vault__fs_exec"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: true);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-websearch__web_browse"]);
    }

    [Fact]
    public void FilterMcpTools_FilesystemInactive_KeepsRawFsTools()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-vault__fs_read"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-vault__fs_read", "mcp__mcp-websearch__web_browse"]);
    }
}