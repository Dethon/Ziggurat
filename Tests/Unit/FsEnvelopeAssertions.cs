using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit;

// The disk tools answer with the shared result type, so a failure is an envelope the model reads
// rather than an exception. These assertions say that once instead of in every per-tool file.
internal static class FsEnvelopeAssertions
{
    public static JsonNode ShouldBeError(this JsonNode node, string errorCode)
    {
        node["ok"]!.GetValue<bool>().ShouldBeFalse(node.ToJsonString());
        node["errorCode"]!.GetValue<string>().ShouldBe(errorCode);
        return node;
    }

    public static JsonNode ShouldBeOk(this JsonNode node)
    {
        node.AsObject().ContainsKey("ok").ShouldBeFalse(node.ToJsonString());
        return node;
    }

    // A backend's answer that should have succeeded, failing with what it answered instead: a bare type
    // mismatch on an Err said nothing of why under load.
    public static FsResult<T>.Ok ShouldBeOk<T>(this FsResult<T> result) where T : class =>
        result as FsResult<T>.Ok
        ?? throw new ShouldAssertException(
            $"expected an Ok but the backend answered {(result is FsResult<T>.Err err ? $"{err.Error.ErrorCode}: {err.Error.Message}" : result.ToString())}");
}