using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The channel's fetch, as the eval answers it. Nothing in an eval dials a channel, so without this
// every attachment would be hydrated into its placeholder and a photographed meal would reach the
// model as the sentence announcing a photo — which a scorecard would then credit to the model.
public sealed class EvalAttachmentsTests : IDisposable
{
    private readonly string _photo = Path.GetTempFileName();

    public EvalAttachmentsTests() => File.WriteAllBytes(_photo, [9, 8, 7]);

    public void Dispose() => File.Delete(_photo);

    private EvalTurn Turn => new()
    {
        Text = "One photo of the meal is attached.",
        Sender = "sugary-app",
        Channel = "sugary",
        Attachments = [new EvalAttachment(_photo, "image/jpeg")]
    };

    [Fact]
    public async Task AnAttachmentOfTheTurn_IsServedFromItsFile_AndCountedAsServed()
    {
        var source = new EvalAttachments(Turn);
        var id = EvalAttachments.References(Turn).Single().Id;

        source.Served.ShouldBeEmpty();
        (await source.FetchAsync("sugary", id, CancellationToken.None)).ShouldBe([9, 8, 7]);
        (await source.FetchAsync("sugary", id, CancellationToken.None)).ShouldBe([9, 8, 7]);

        // Distinct: a turn that calls a tool sends the photo again with its second request.
        source.Served.ShouldBe([id]);
    }

    [Fact]
    public async Task AnUnknownId_OrAnotherChannel_IsNothing()
    {
        var source = new EvalAttachments(Turn);
        var id = EvalAttachments.References(Turn).Single().Id;

        (await source.FetchAsync("sugary", "not-an-attachment", CancellationToken.None)).ShouldBeNull();
        (await source.FetchAsync("signalr", id, CancellationToken.None)).ShouldBeNull();
        source.Served.ShouldBeEmpty();
    }

    [Fact]
    public void References_KeepTheOrderTheAttachmentsWereGivenIn_WithIdsOfTheirOwn()
    {
        var turn = Turn with
        {
            Attachments = [new EvalAttachment(_photo, "image/jpeg"), new EvalAttachment(_photo, "image/png")]
        };

        var references = EvalAttachments.References(turn);

        references.Select(r => r.MediaType).ShouldBe(["image/jpeg", "image/png"]);
        references.Select(r => r.Id).Distinct().Count().ShouldBe(2);
    }
}