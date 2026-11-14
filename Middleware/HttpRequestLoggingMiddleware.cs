using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace elastic_obs.Middleware;

internal sealed class HttpRequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<HttpRequestLoggingMiddleware> logger)
{
    private const int MaxLoggedBodyBytes = 16 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var request = context.Request;
        var queryString = RedactQueryString(request.QueryString.Value);
        var requestName = $"{request.Method} {request.Path}";
        var requestBody = await ReadRequestBodyAsync(request, context.RequestAborted);

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["HttpContent"] = new Dictionary<string, object?>
            {
                ["queryString"] = queryString,
                ["body"] = requestBody,
                ["headers"] = CaptureHeaders(request.Headers)
            }
        }))
        {
            logger.LogInformation("--> IN {Request}", requestName);
        }

        var originalBody = context.Response.Body;
        using var capturedBody = new CapturedResponseStream(originalBody, MaxLoggedBodyBytes);
        context.Response.Body = capturedBody;

        try
        {
            await next(context);
        }
        catch
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }

            throw;
        }
        finally
        {
            stopwatch.Stop();
            context.Response.Body = originalBody;

            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["duration"] = stopwatch.Elapsed.TotalMilliseconds,
                ["httpStatus"] = context.Response.StatusCode,
                ["HttpContent"] = new Dictionary<string, object?>
                {
                    ["body"] = SanitizeBody(
                        capturedBody.GetCapturedBytes(),
                        context.Response.ContentType,
                        capturedBody.IsTruncated),
                    ["headers"] = CaptureHeaders(context.Response.Headers)
                }
            }))
            {
                logger.LogInformation("--> OUT {Request}", requestName);
            }
        }
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
        {
            return null;
        }

        if (!IsTextContentType(request.ContentType))
        {
            return request.ContentLength is null && HttpMethods.IsGet(request.Method)
                ? null
                : "[body omitted: unsupported or non-text content type]";
        }

        request.EnableBuffering();
        var originalPosition = request.Body.Position;

        try
        {
            request.Body.Position = 0;
            var buffer = new byte[MaxLoggedBodyBytes + 1];
            var bytesRead = 0;

            while (bytesRead < buffer.Length)
            {
                var read = await request.Body.ReadAsync(
                    buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                    cancellationToken);

                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }

            var truncated = bytesRead > MaxLoggedBodyBytes;
            var capturedBytes = buffer.AsSpan(0, Math.Min(bytesRead, MaxLoggedBodyBytes)).ToArray();
            return SanitizeBody(capturedBytes, request.ContentType, truncated);
        }
        finally
        {
            request.Body.Position = originalPosition;
        }
    }

    private static string? SanitizeBody(byte[] body, string? contentType, bool truncated)
    {
        if (body.Length == 0)
        {
            return null;
        }

        if (!IsTextContentType(contentType))
        {
            return "[body omitted: unsupported or non-text content type]";
        }

        var text = Encoding.UTF8.GetString(body);
        var mediaType = contentType!.Split(';', 2)[0].Trim();

        if (IsJsonContentType(mediaType))
        {
            if (truncated)
            {
                return "[JSON body omitted because it exceeded the logging limit]";
            }

            try
            {
                var json = JsonNode.Parse(text);
                RedactSensitiveJsonValues(json);
                return json?.ToJsonString() ?? text;
            }
            catch (JsonException)
            {
                return "[JSON body omitted because it could not be safely redacted]";
            }
        }

        if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return truncated
                ? "[form body omitted because it exceeded the logging limit]"
                : RedactQueryString(text);
        }

        return truncated
            ? $"{text}[truncated at {MaxLoggedBodyBytes} bytes]"
            : text;
    }

    private static string RedactQueryString(string? queryString)
    {
        if (string.IsNullOrEmpty(queryString))
        {
            return string.Empty;
        }

        var parsed = QueryHelpers.ParseQuery(queryString);
        var redacted = parsed.SelectMany(parameter =>
            parameter.Value.Select(value =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(IsSensitiveName(parameter.Key) ? "[REDACTED]" : value ?? string.Empty)}"));

        var result = string.Join("&", redacted);
        return queryString.StartsWith('?') ? $"?{result}" : result;
    }

    private static Dictionary<string, string[]> CaptureHeaders(IHeaderDictionary headers)
    {
        var captured = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            var values = header.Value.Select(value => value ?? string.Empty).ToArray();

            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                captured[header.Key] = values.Select(value => RedactCookieHeader(value, false)).ToArray();
            }
            else if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                captured[header.Key] = values.Select(value => RedactCookieHeader(value, true)).ToArray();
            }
            else if (IsSensitiveName(header.Key))
            {
                captured[header.Key] = ["[REDACTED]"];
            }
            else
            {
                captured[header.Key] = values;
            }
        }

        return captured;
    }

    private static string RedactCookieHeader(string value, bool isSetCookie)
    {
        var segments = value.Split(';');

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index].Trim();
            var separator = segment.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var cookieName = segment[..separator].Trim();
            var cookieValue = isSetCookie && index > 0
                ? segment[(separator + 1)..].Trim()
                : "[REDACTED]";

            segments[index] = $"{cookieName}={cookieValue}";

            if (isSetCookie)
            {
                break;
            }
        }

        return string.Join("; ", segments.Select(segment => segment.Trim()));
    }

    private static void RedactSensitiveJsonValues(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (IsSensitiveName(property.Key))
                {
                    jsonObject[property.Key] = "[REDACTED]";
                }
                else
                {
                    RedactSensitiveJsonValues(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                RedactSensitiveJsonValues(item);
            }
        }
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("passwd", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || IsJsonContentType(mediaType)
            || mediaType.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/graphql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJsonContentType(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private sealed class CapturedResponseStream(Stream innerStream, int maxCaptureBytes) : Stream
    {
        private readonly MemoryStream _captured = new();

        public bool IsTruncated { get; private set; }

        public override bool CanRead => innerStream.CanRead;

        public override bool CanSeek => innerStream.CanSeek;

        public override bool CanWrite => innerStream.CanWrite;

        public override long Length => innerStream.Length;

        public override long Position
        {
            get => innerStream.Position;
            set => innerStream.Position = value;
        }

        public byte[] GetCapturedBytes() => _captured.ToArray();

        public override void Flush() => innerStream.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            innerStream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            innerStream.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            innerStream.Seek(offset, origin);

        public override void SetLength(long value) => innerStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            innerStream.Write(buffer, offset, count);
            Capture(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            innerStream.Write(buffer);
            Capture(buffer);
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await innerStream.WriteAsync(buffer, offset, count, cancellationToken);
            Capture(buffer.AsSpan(offset, count));
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await innerStream.WriteAsync(buffer, cancellationToken);
            Capture(buffer.Span);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _captured.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<byte> bytes)
        {
            var remaining = maxCaptureBytes - (int)_captured.Length;
            var bytesToCapture = Math.Min(remaining, bytes.Length);

            if (bytesToCapture > 0)
            {
                _captured.Write(bytes[..bytesToCapture]);
            }

            if (bytesToCapture < bytes.Length)
            {
                IsTruncated = true;
            }
        }
    }
}
