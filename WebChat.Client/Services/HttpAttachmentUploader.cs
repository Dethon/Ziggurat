using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

// One file per request, deliberately: the web host's default body cap is below the combined size
// of a full message's attachments at the configured maximum. The hub's own message size limit is
// untouched, because bytes never ride the hub.
//
// The upload store lives on the channel server, which is where the hub lives too — never on the
// host that served this page. So the address is resolved the same way the hub connection resolves
// its own: same origin behind the reverse proxy, the configured agent URL otherwise.
public sealed class HttpAttachmentUploader(
    HttpClient httpClient,
    AttachmentEndpointResolver endpoints,
    ILogger<HttpAttachmentUploader> logger) : IAttachmentUploader
{
    public async Task<UploadOutcome> UploadAsync(
        string topicId,
        string ticket,
        PickedFile file,
        Action<int> onProgress,
        CancellationToken ct)
    {
        try
        {
            var url = await ResolveUploadUrlAsync(topicId);
            await using var content = await file.OpenRead(ct);
            using var body = new MultipartFormDataContent();
            var part = new StreamContent(new ProgressStream(content, file.SizeBytes, onProgress));
            part.Headers.ContentType = new MediaTypeHeaderValue(file.MediaType);
            body.Add(part, "file", file.FileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            request.Headers.Add(AttachmentEndpointPaths.TicketHeader, ticket);

            using var response = await httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new UploadOutcome(null, await DescribeAsync(response, file, ct));
            }

            onProgress(100);
            var reference = await response.Content.ReadFromJsonAsync<AttachmentReference>(ct);
            return reference is null
                ? new UploadOutcome(null, $"{file.FileName} was accepted but came back without a reference.")
                : new UploadOutcome(reference, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uploading {FileName} failed", file.FileName);
            return new UploadOutcome(null, $"{file.FileName} could not be uploaded.");
        }
    }

    private async Task<string> ResolveUploadUrlAsync(string topicId) =>
        $"{await endpoints.ResolveAsync(AttachmentEndpointPaths.Attachments)}"
        + $"?{AttachmentEndpointPaths.TopicQueryParameter}={Uri.EscapeDataString(topicId)}";

    private static async Task<string> DescribeAsync(
        HttpResponseMessage response, PickedFile file, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body)
            ? response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => $"{file.FileName} was refused: the upload permission has expired.",
                _ => $"{file.FileName} was refused by the server."
            }
            : body;
    }

    // Progress is the bytes leaving the browser, which is what a person watching a large file
    // wants to see. Wrapping the stream keeps that out of the upload itself.
    private sealed class ProgressStream(Stream inner, long total, Action<int> onProgress) : Stream
    {
        private long _read;
        private int _lastReported = -1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            Report(count);
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Report(read);
            return read;
        }

        private void Report(int count)
        {
            _read += count;
            if (total <= 0)
            {
                return;
            }

            var percent = (int)Math.Min(99, _read * 100 / total);
            if (percent != _lastReported)
            {
                _lastReported = percent;
                onProgress(percent);
            }
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}