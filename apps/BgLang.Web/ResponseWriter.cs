using System.Text.Json;

namespace BgLang.Web;

/// <summary>Admits the whole encoded response before headers; retains no payload buffer.</summary>
internal sealed class ResponseWriter
{
    internal long MaximumBytes { get; }

    internal ResponseWriter(long maximumBytes)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        MaximumBytes = maximumBytes;
    }

    internal async Task WriteAsync(HttpContext context, ApiEnvelope result, string? format,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        format ??= result.ApiVersion == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat;
        JsonSerializerOptions options = HttpContract.Json;
        if (format != HttpContract.RawFormat)
        {
            var shared = new SharedDonorResponse(result, format == SharedDonorResponse.AnalysisFormat, cancellation);
            result = shared.Envelope;
            options = shared.Json;
        }
        // Identical immutable result/options in both passes. No giant byte[], JSON string or disk dump.
        using var count = new BoundedBody(Stream.Null, MaximumBytes, cancellation);
        JsonSerializer.Serialize(count, result, options);
        cancellation.ThrowIfCancellationRequested();
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = count.Bytes;
        using var body = new BoundedBody(context.Response.Body, count.Bytes, cancellation);
        await JsonSerializer.SerializeAsync(body, result, options, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (body.Bytes != count.Bytes) throw new InvalidOperationException("Response changed after byte admission.");
    }

    private sealed class BoundedBody(Stream output, long maximumBytes, CancellationToken cancellation) : Stream
    {
        internal long Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }

        private void Admit(int bytes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (bytes > maximumBytes - Bytes)
                throw new ApiRequestException(413, "response-too-large",
                    "Целият отговор надвишава byte профила на хоста. Не е върнат съкратен анализ.");
            Bytes += bytes;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Admit(buffer.Length);
            output.Write(buffer);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Admit(buffer.Length);
            return output.WriteAsync(buffer, cancellationToken);
        }
        public override void Flush() { cancellation.ThrowIfCancellationRequested(); output.Flush(); }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellation.ThrowIfCancellationRequested();
            return output.FlushAsync(cancellationToken);
        }
        // The HTTP response stream belongs to ASP.NET; disposing this guard must not close it.
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
