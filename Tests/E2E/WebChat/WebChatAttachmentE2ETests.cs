using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The feature's one end-to-end test: attach an image, send it, see it in the transcript, reload,
// see it still there. The transcript is a record and not a session.
[Collection("WebChatE2E")]
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
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // The file-input API rather than a real dialog: one control serves the picker, the paste
        // and the drop, so driving the input is driving all three.
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "e2e-photo.png",
            MimeType = "image/png",
            Buffer = _onePixelPng
        });

        // Uploaded and ready: the chip shows the name and the send button comes back.
        await Assertions.Expect(page.Locator(".composer-attachment"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("What is in this picture?");
        await chatInput.PressAsync("Enter");

        var attachment = page.Locator(".chat-message.user .message-attachments");
        await attachment.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var afterReload = page.Locator(".chat-message.user .message-attachments");
        await afterReload.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await afterReload.CountAsync()).ShouldBeGreaterThan(0);
    }
}