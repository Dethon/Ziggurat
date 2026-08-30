using System.Text.Json;
using System.Text.Json.Nodes;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// The one production probe: each rung is its own crossing into the page that listed the picture.
// In-page rungs run through the page so their requests are the page's own -- Camoufox holds the
// cookies, the referer and the fingerprint, and a great many images are served only to a request
// carrying them; a bare HttpClient would answer 403 or a placeholder pixel on exactly the pages
// this stack is earning its keep. No rung decides anything: the descent in ImageAcquisition does.
//
// Bytes come back as base64 because the evaluate boundary carries text, not binary; each rung
// answers one JSON object rather than a delimited string, because the strings a page offers are
// somebody else's text and may carry any delimiter a hand-rolled shape would pick.
internal sealed class PlaywrightImageProbe(IPage page) : IImagePageProbe
{
    public async Task<LocatedImage?> LocateAsync(string imageRef)
    {
        var payload = await page.EvaluateAsync<string?>(
            $$"""
              (ref) => {
                  const img = document.querySelector(`[{{PageImageEntry.RefAttribute}}="${ref}"]`);
                  if (!img) return null;
                  const src = img.getAttribute('src');
                  if (!src) return null;
                  let url = null, sameOrigin = false;
                  try {
                      const resolved = new URL(src, location.href);
                      url = resolved.toString();
                      sameOrigin = resolved.origin === location.origin;
                  } catch { /* an address no parser accepts: the descent stops at the locate */ }
                  return JSON.stringify({
                      alt: img.getAttribute('alt'),
                      caption: img.closest('figure')?.querySelector('figcaption')?.textContent ?? null,
                      title: img.getAttribute('title'),
                      linkText: img.closest('a')?.textContent ?? null,
                      src,
                      url,
                      sameOrigin,
                      w: parseInt(img.getAttribute('{{PageImageEntry.WidthAttribute}}') ?? '0', 10) || 0,
                      h: parseInt(img.getAttribute('{{PageImageEntry.HeightAttribute}}') ?? '0', 10) || 0
                  });
              }
              """,
            imageRef);

        if (payload is null)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(payload)?.AsObject();
            var facts = new ImageLabelFacts(
                Alt: (string?)node?["alt"],
                Caption: (string?)node?["caption"],
                Title: (string?)node?["title"],
                LinkText: (string?)node?["linkText"],
                Src: (string?)node?["src"],
                Width: (int?)node?["w"] ?? 0,
                Height: (int?)node?["h"] ?? 0);
            return new LocatedImage(facts, (string?)node?["url"], (bool?)node?["sameOrigin"] is true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A payload the script never wrote: the picture is located but nothing below the
            // locate can run, which the descent answers as the site refusing.
            return new LocatedImage(new ImageLabelFacts(null, null, null, null, null, 0, 0), null, false);
        }
    }

    public async Task<WireRungAnswer?> WireFetchAsync(string url, bool withCredentials)
    {
        var payload = await page.EvaluateAsync<string?>(
            """
            async ({ url, credentials }) => {
                try {
                    const response = await fetch(url, { credentials });
                    const mediaType = response.headers.get('content-type');
                    if (!response.ok) return JSON.stringify({ ok: false, mediaType });
                    const bytes = new Uint8Array(await response.arrayBuffer());
                    let binary = '';
                    for (let i = 0; i < bytes.length; i++) {
                        binary += String.fromCharCode(bytes[i]);
                    }
                    return JSON.stringify({ ok: true, mediaType, data: btoa(binary) });
                } catch {
                    return null;
                }
            }
            """,
            new { url, credentials = withCredentials ? "include" : "omit" });

        return ParseWireAnswer(payload);
    }

    public async Task<CanvasRungAnswer> CanvasReadAsync(string imageRef, string url)
    {
        var payload = await page.EvaluateAsync<string?>(
            $$"""
              async ({ ref, url }) => {
                  const img = document.querySelector(`[{{PageImageEntry.RefAttribute}}="${ref}"]`);
                  if (!img) return JSON.stringify({ kind: 'failed' });
                  try {
                      const drawn = await new Promise((resolve) => {
                          const probe = new Image();
                          probe.crossOrigin = 'anonymous';
                          probe.onload = () => resolve(probe);
                          probe.onerror = () => resolve(null);
                          // Already in the page and decoded: this is a cache hit, not a second
                          // download, whenever the element itself has loaded.
                          probe.src = url;
                      });

                      const source = drawn
                          || (img.complete && img.naturalWidth > 0 ? img : null);
                      // No probe result and no decoded element either: the bytes never arrived
                      // at all — a dead link, not a CDN guarding its pixels.
                      if (!source) return JSON.stringify({ kind: 'never-loaded' });

                      // A source with no intrinsic size — a viewBox-only SVG reports
                      // naturalWidth 0 — would size the canvas 0x0 and leave as a zero-byte
                      // "success" the vision provider rejects whole. The pixels are on screen,
                      // so answer tainted and let the rungs below the canvas have it.
                      if (!source.naturalWidth || !source.naturalHeight) {
                          return JSON.stringify({ kind: 'tainted' });
                      }

                      const canvas = document.createElement('canvas');
                      canvas.width = source.naturalWidth;
                      canvas.height = source.naturalHeight;
                      canvas.getContext('2d').drawImage(source, 0, 0);

                      // Throws a SecurityError on a tainted canvas -- an image the browser will
                      // show but not let script read. The pixels are on screen all the same.
                      const dataUrl = canvas.toDataURL('image/png');
                      return JSON.stringify({ kind: 'drawn', data: dataUrl.split(',')[1] });
                  } catch (e) {
                      return JSON.stringify({ kind: e && e.name === 'SecurityError' ? 'tainted' : 'failed' });
                  }
              }
              """,
            new { @ref = imageRef, url });

        try
        {
            var node = payload is null ? null : JsonNode.Parse(payload)?.AsObject();
            var data = (string?)node?["data"];
            return (string?)node?["kind"] switch
            {
                "drawn" when data is not null =>
                    new CanvasRungAnswer(CanvasOutcome.Drawn, Convert.FromBase64String(data)),
                "tainted" => new CanvasRungAnswer(CanvasOutcome.Tainted, null),
                "never-loaded" => new CanvasRungAnswer(CanvasOutcome.NeverLoaded, null),
                _ => new CanvasRungAnswer(CanvasOutcome.Failed, null)
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return new CanvasRungAnswer(CanvasOutcome.Failed, null);
        }
    }

    // CORS binds script inside the page and nothing else: the context's own request client pulls
    // the same address with the same cookies and keeps the bytes exactly as served — the JPL
    // gallery, whose CDN sends no ACAO header at all, is the live page this bought back.
    public async Task<WireRungAnswer?> ContextRequestAsync(string url)
    {
        try
        {
            var response = await page.Context.APIRequest.GetAsync(url, new() { Timeout = 10_000 });
            try
            {
                var mediaType = response.Headers.GetValueOrDefault("content-type");
                if (!response.Ok)
                {
                    return new WireRungAnswer(false, mediaType, null);
                }

                var bytes = await response.BodyAsync();
                return new WireRungAnswer(true, mediaType, bytes);
            }
            finally
            {
                // Playwright retains the body until the response is disposed or its context dies,
                // and this context lives until reconnect — undisposed, every re-request kept its
                // full image in memory for the life of the process.
                await response.DisposeAsync();
            }
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    // The compositor already painted these pixels, and a screenshot of the element reads them
    // back without CORS having a say. What leaves is the rendered box re-encoded as PNG at the
    // size the page shows it.
    public async Task<byte[]?> ElementScreenshotAsync(string imageRef)
    {
        try
        {
            return await page.Locator($"[{PageImageEntry.RefAttribute}='{imageRef}']")
                .ScreenshotAsync(new() { Type = ScreenshotType.Png, Timeout = 10_000 });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return null;
        }
    }

    private static WireRungAnswer? ParseWireAnswer(string? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(payload)?.AsObject();
            var data = (string?)node?["data"];
            return new WireRungAnswer(
                Ok: (bool?)node?["ok"] is true,
                MediaType: (string?)node?["mediaType"],
                Bytes: data is null ? null : Convert.FromBase64String(data));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}