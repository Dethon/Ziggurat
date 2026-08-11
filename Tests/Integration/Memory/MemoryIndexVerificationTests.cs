using Infrastructure.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

public class MemoryIndexVerificationTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task AWrongWidthIndex_FailsStartupNamingBothValues()
    {
        var indexName = await CreateIndexAsync(dimension: 1536);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => StartVerificationAsync(indexName, configuredDimension: 1024));

        ex.Message.ShouldContain("1024");
        ex.Message.ShouldContain("1536");
    }

    [Fact]
    public async Task AnIndexCarryingAFieldTheCodeNoLongerCreates_StillStarts()
    {
        // The live production index carries a leftover tag field from a superseded feature.
        // Only the vector field's dimension is compared, so a healthy index still starts.
        var indexName = await CreateIndexAsync(dimension: 1024, withLeftoverField: true);

        await Should.NotThrowAsync(() => StartVerificationAsync(indexName, configuredDimension: 1024));
    }

    [Fact]
    public async Task AnIndexWithNoVectorFieldAtAll_FailsStartupRatherThanReadingAsAbsent()
    {
        var indexName = $"idx:novector:{Guid.NewGuid():N}";
        var ft = fixture.Connection.GetDatabase().FT();
        await ft.CreateAsync(
            indexName,
            new FTCreateParams().On(IndexDataType.HASH).Prefix($"{indexName}:"),
            new Schema().AddTagField("userId", separator: "|"));

        // The store only creates an index when reading the live one fails, so this one would
        // never be repaired — it would sit there failing every search into recall's catch-all.
        await Should.ThrowAsync<InvalidOperationException>(
            () => StartVerificationAsync(indexName, configuredDimension: 1024));
    }

    [Fact]
    public async Task AnAbsentIndex_Starts()
    {
        await Should.NotThrowAsync(
            () => StartVerificationAsync($"idx:absent:{Guid.NewGuid():N}", configuredDimension: 1024));
    }

    private Task StartVerificationAsync(string indexName, int configuredDimension)
    {
        var verification = new MemoryIndexVerification(
            fixture.Connection,
            indexName,
            configuredDimension,
            NullLogger<MemoryIndexVerification>.Instance);
        return verification.StartAsync(CancellationToken.None);
    }

    private async Task<string> CreateIndexAsync(int dimension, bool withLeftoverField = false)
    {
        var indexName = $"idx:verify:{Guid.NewGuid():N}";
        var schema = new Schema()
            .AddTagField("userId", separator: "|")
            .AddTextField("content");

        if (withLeftoverField)
        {
            schema.AddTagField("supersededTag", separator: ",");
        }

        schema.AddVectorField("embedding", Schema.VectorField.VectorAlgo.HNSW, new Dictionary<string, object>
        {
            ["TYPE"] = "FLOAT32",
            ["DIM"] = dimension,
            ["DISTANCE_METRIC"] = "COSINE"
        });

        var ft = fixture.Connection.GetDatabase().FT();
        await ft.CreateAsync(
            indexName,
            new FTCreateParams().On(IndexDataType.HASH).Prefix($"{indexName}:"),
            schema);
        return indexName;
    }
}