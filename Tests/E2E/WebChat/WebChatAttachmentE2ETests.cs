using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The feature's one end-to-end test: attach an image, send it, see it in the transcript, reload,
// see it still there. The transcript is a record and not a session.
[Collection(WebChatE2ECollections.Attachments)]
[Trait("Category", "E2E")]
public class WebChatAttachmentE2ETests(WebChatE2EFixture fixture)
{
    // A one-pixel PNG. Small enough to upload instantly and real enough that the browser gives it
    // an image content type.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [SkippableFact]
    public async Task AnAttachedImage_IsInTheTranscriptAndStillThereAfterAReload()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // The stack's default agent runs a text-only model, and the composer refuses to send an
        // image to a model that cannot read one — this test runs on the vision agent.
        await page.Locator(".agent-seg", new PageLocatorOptions { HasText = "Vision Agent" })
            .ClickAsync(new LocatorClickOptions { Timeout = 10_000 });

        // The file-input API rather than a real dialog: one control serves the picker, the paste
        // and the drop, so driving the input is driving all three.
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "e2e-photo.png",
            MimeType = "image/png",
            Buffer = _onePixelPng
        });

        // Uploaded and ready — the bare chip is not enough, a refused upload keeps its chip too,
        // with a failed class and the send then travelling without the file.
        await Assertions.Expect(page.Locator(".composer-attachment.ready"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // A capability refusal disables the send silently, so without this check a model that
        // cannot read images fails the test as an unexplained timeout further down.
        await Assertions.Expect(page.Locator(".composer-refusal")).ToBeHiddenAsync();

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("What is in this picture?");
        await chatInput.PressAsync("Enter");

        var attachment = page.Locator(".chat-message.user .message-attachments");
        await attachment.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // The reply is the proof the turn reached the agent and its record was written; reloading
        // before it races the history read against the agent's own persistence. 90s for the same
        // reason as SendMessage_AppearsInChat: reasoning plus shared provider slots can hold the
        // first text token past half a minute.
        await Assertions.Expect(page.Locator(".chat-message.assistant .message-content").First)
            .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 90_000 });

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // A reload restores the record, not the session: nothing is selected until the person
        // opens a conversation, so open it again before asking after its attachments.
        await WebChatE2ETests.DismissApprovalOverlayAsync(page);
        // A conversation started by attaching a file is named after the file only until the
        // opening message arrives, and that rename is persisted, so the row carries the text.
        await page.Locator(".topic-item", new PageLocatorOptions { HasText = "What is in this picture?" })
            .First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        var afterReload = page.Locator(".chat-message.user .message-attachments");
        await afterReload.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await afterReload.CountAsync()).ShouldBeGreaterThan(0);
    }

    // The attach control is a label wearing the button class, so it only looks like the rest of
    // the composer for as long as it takes the same size as them. The phone breakpoint shrinks
    // every button, and a control that opts out of that shrink stands taller than the field and
    // the control beside it — which is the microphone until something is typed, and Send after.
    [SkippableFact]
    public async Task OnAPhoneViewport_TheAttachButtonIsAsTallAsTheSendButton()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var attach = page.Locator("label.attach-button");
        var micBox = await page.Locator("[data-testid=dictation-mic]").BoundingBoxAsync();
        var attachBox = await attach.BoundingBoxAsync();

        attachBox.ShouldNotBeNull();
        micBox.ShouldNotBeNull();
        attachBox.Height.ShouldBe(micBox.Height, tolerance: 1);

        // And the send button, which takes the microphone's place the moment there is text.
        await page.Locator("textarea.chat-input").FillAsync("something to send");
        var sendBox = await page.Locator("button.btn-primary", new PageLocatorOptions { HasText = "Send" })
            .BoundingBoxAsync();

        sendBox.ShouldNotBeNull();
        (await attach.BoundingBoxAsync())!.Height.ShouldBe(sendBox.Height, tolerance: 1);
    }
}