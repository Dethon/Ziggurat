using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The byte-acquisition descent, asserted against a faked probe: each step down trades fidelity
// for reach, and C# decides every one. These used to be assertable only through a live browser --
// three of the rungs only against third-party sites having a good day.
public class ImageAcquisitionTests
{
    private static readonly byte[] _served = [1, 2, 3, 4];
    private static readonly byte[] _pixels = [9, 8, 7];

    [Fact]
    public async Task BytesTheWireCanObtain_LeaveExactlyAsServed()
    {
        var probe = Probe() with { Wire = new WireRungAnswer(true, "image/jpeg", _served) };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/jpeg");
        fetched.Bytes.ShouldBe(_served);
        probe.Rungs.ShouldBe(["locate", "wire"]);
    }

    [Fact]
    public async Task AWireFailure_FallsToTheCanvas()
    {
        var probe = Probe() with { Wire = null, Canvas = Drawn(_pixels) };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/png");
        fetched.Bytes.ShouldBe(_pixels);
        probe.Rungs.ShouldBe(["locate", "wire", "canvas"]);
    }

    [Fact]
    public async Task ANonRasterWireAnswer_FallsToTheCanvas()
    {
        // As-served SVG made the vision provider refuse the whole request; only wire rasters
        // leave as the file, everything else leaves as pixels.
        var probe = Probe() with
        {
            Wire = new WireRungAnswer(true, "image/svg+xml", _served),
            Canvas = Drawn(_pixels)
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.MediaType.ShouldBe("image/png");
        fetched.Bytes.ShouldBe(_pixels);
    }

    [Theory]
    [InlineData(" image/png ")]
    [InlineData("image/png; charset=utf-8")]
    [InlineData(" image/png ; charset=utf-8")]
    public async Task AMediaTypeWithStrayWhitespace_IsJudgedTheSameOnTheWire(string mediaType)
    {
        // The acceptance rule is stated once and compared once: a header quirk cannot pass one
        // copy and fail another, because there is no other copy.
        var probe = Probe() with { Wire = new WireRungAnswer(true, mediaType, _served) };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/png");
        fetched.Bytes.ShouldBe(_served);
    }

    [Fact]
    public async Task AMediaTypeWithStrayWhitespace_IsJudgedTheSameOnTheContextRequest()
    {
        var probe = Probe() with
        {
            Wire = null,
            Canvas = Tainted(),
            Context = new WireRungAnswer(true, " image/jpeg ; q=1", _served)
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/jpeg");
        fetched.Bytes.ShouldBe(_served);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task AWireAnswerWithNoContentType_LeavesAsAJpeg(string? mediaType)
    {
        // A CDN that sends no content-type at all -- header absent or blank -- has always been
        // taken at the wire's word as a jpeg on this rung; the module preserves that rather than
        // paying the canvas re-encode. The blank spelling is how the old in-page script saw an
        // absent header, so it stays a named fact rather than becoming a quiet re-encode.
        var probe = Probe() with { Wire = new WireRungAnswer(true, mediaType, _served) };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task ATaintedCanvas_FallsToTheContextRequest()
    {
        // CORS binds script inside the page and nothing else: the context's request client pulls
        // the same address with the same cookies and keeps the bytes exactly as served.
        var probe = Probe() with
        {
            Wire = null,
            Canvas = Tainted(),
            Context = new WireRungAnswer(true, "image/jpeg", _served)
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/jpeg");
        fetched.Bytes.ShouldBe(_served);
        probe.Rungs.ShouldBe(["locate", "wire", "canvas", "context"]);
    }

    [Fact]
    public async Task AContextRequestRefusal_FallsToTheElementScreenshot()
    {
        var probe = Probe() with
        {
            Wire = null,
            Canvas = Tainted(),
            Context = null,
            Screenshot = _pixels
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.Success);
        fetched.MediaType.ShouldBe("image/png");
        fetched.Bytes.ShouldBe(_pixels);
        probe.Rungs.ShouldBe(["locate", "wire", "canvas", "context", "screenshot"]);
    }

    [Fact]
    public async Task EveryRungRefusing_IsTheSiteRefusing()
    {
        var probe = Probe() with { Wire = null, Canvas = Tainted(), Context = null, Screenshot = null };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public async Task AnImageThatNeverLoaded_AnswersItsOwnWallNotTheSiteRefusedOne()
    {
        // A dead link invites no retry; a CDN guarding pixels the page displays invites two more
        // rungs. Confusing the two sent the model round pointless retries of rotted addresses.
        var probe = Probe() with
        {
            Wire = null,
            Canvas = new CanvasRungAnswer(CanvasOutcome.NeverLoaded, null),
            Context = new WireRungAnswer(true, "image/jpeg", _served),
            Screenshot = _pixels
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.NeverLoaded);
        probe.Rungs.ShouldBe(["locate", "wire", "canvas"]);
    }

    [Fact]
    public async Task ACanvasFailure_IsTheSiteRefusing()
    {
        var probe = Probe() with { Wire = null, Canvas = new CanvasRungAnswer(CanvasOutcome.Failed, null) };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.SiteRefused);
    }

    [Fact]
    public async Task ARefNamingNothing_IsNotAnImageRef()
    {
        var probe = Probe() with { Located = null };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-7");

        fetched.Status.ShouldBe(ImageFetchStatus.NotAnImageRef);
        probe.Rungs.ShouldBe(["locate"]);
    }

    [Fact]
    public async Task AnAddressThatWouldNotResolve_IsTheSiteRefusing()
    {
        // The page carries a src no URL parser accepts: nothing below the locate can run, and
        // the browser itself is what will not show it.
        var probe = Probe() with { Located = Located() with { Url = null } };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Status.ShouldBe(ImageFetchStatus.SiteRefused);
        probe.Rungs.ShouldBe(["locate"]);
    }

    [Fact]
    public async Task ASameOriginAddress_IsFetchedWithThePagesCredentials()
    {
        var probe = Probe() with
        {
            Located = Located() with { SameOrigin = true },
            Wire = new WireRungAnswer(true, "image/jpeg", _served)
        };

        await ImageAcquisition.FetchAsync(probe, "i-1");

        probe.WireCredentialed.ShouldBe(true);
    }

    [Fact]
    public async Task ACrossOriginAddress_IsFetchedAnonymously()
    {
        // The big image CDNs answer ACAO *, and a wildcard is exactly what a credentialed request
        // is rejected against. This broke the first implementation; it is not to be "fixed".
        var probe = Probe() with
        {
            Located = Located() with { SameOrigin = false },
            Wire = new WireRungAnswer(true, "image/jpeg", _served)
        };

        await ImageAcquisition.FetchAsync(probe, "i-1");

        probe.WireCredentialed.ShouldBe(false);
    }

    [Fact]
    public async Task TheOneLadder_NamesEveryAnswerThatCarriesBytes()
    {
        var probe = Probe() with
        {
            Located = Located() with
            {
                Facts = new ImageLabelFacts("A harbour at dusk", null, null, null, "/p.jpg", 300, 200)
            },
            Wire = new WireRungAnswer(true, "image/jpeg", _served)
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Label.ShouldBe("A harbour at dusk");
    }

    [Fact]
    public async Task TheScreenshotAnswer_CarriesTheSameLabelTheEntryChose()
    {
        var probe = Probe() with
        {
            Located = Located() with
            {
                Facts = new ImageLabelFacts("A rover on Mars", null, null, null, "/p.png", 300, 200)
            },
            Wire = null,
            Canvas = Tainted(),
            Context = null,
            Screenshot = _pixels
        };

        var fetched = await ImageAcquisition.FetchAsync(probe, "i-1");

        fetched.Label.ShouldBe("A rover on Mars");
    }

    private static LocatedImage Located() => new(
        new ImageLabelFacts("A picture", null, null, null, "/pic.jpg", 300, 200),
        "https://cdn.test/pic.jpg",
        SameOrigin: false);

    private static CanvasRungAnswer Drawn(byte[] png) => new(CanvasOutcome.Drawn, png);

    private static CanvasRungAnswer Tainted() => new(CanvasOutcome.Tainted, null);

    private static FakeProbe Probe() => new() { Located = Located() };

    // The seam the descent runs against in production, answered from fields instead of a page.
    // Rungs records which steps were consulted, in order -- the observable half of "C# decides
    // every step down".
    private sealed record FakeProbe : IImagePageProbe
    {
        public FakeProbe() { }

        // The synthesized copy constructor would hand every `with` clone the original's Rungs
        // list, so two probes forked from one seed would record into shared state. Each clone
        // starts its own recording instead -- assigned here because a copy constructor skips
        // the field initializers.
        private FakeProbe(FakeProbe original)
        {
            Located = original.Located;
            Wire = original.Wire;
            Canvas = original.Canvas;
            Context = original.Context;
            Screenshot = original.Screenshot;
            Rungs = [];
        }

        public LocatedImage? Located { get; init; }
        public WireRungAnswer? Wire { get; init; }
        public CanvasRungAnswer Canvas { get; init; } = new(CanvasOutcome.Failed, null);
        public WireRungAnswer? Context { get; init; }
        public byte[]? Screenshot { get; init; }

        public List<string> Rungs { get; } = [];
        public bool? WireCredentialed { get; private set; }

        public Task<LocatedImage?> LocateAsync(string imageRef)
        {
            Rungs.Add("locate");
            return Task.FromResult(Located);
        }

        public Task<WireRungAnswer?> WireFetchAsync(string url, bool withCredentials)
        {
            Rungs.Add("wire");
            WireCredentialed = withCredentials;
            return Task.FromResult(Wire);
        }

        public Task<CanvasRungAnswer> CanvasReadAsync(string imageRef, string url)
        {
            Rungs.Add("canvas");
            return Task.FromResult(Canvas);
        }

        public Task<WireRungAnswer?> ContextRequestAsync(string url)
        {
            Rungs.Add("context");
            return Task.FromResult(Context);
        }

        public Task<byte[]?> ElementScreenshotAsync(string imageRef)
        {
            Rungs.Add("screenshot");
            return Task.FromResult(Screenshot);
        }
    }
}