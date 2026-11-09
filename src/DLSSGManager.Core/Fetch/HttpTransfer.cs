using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace DLSSGManager;

/// <summary>
/// Streams one URL into a file: redirect checking, size caps, stall detection, pause, and resuming a
/// dropped connection from the byte it reached.
/// </summary>
internal static class HttpTransfer
{
    /// <summary>A source that sends nothing for this long is abandoned, so the next one gets its turn.</summary>
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How many times one file is continued from where a dropped connection left it.</summary>
    public const int MaxResumes = 3;

    /// <summary>
    /// Sends a GET, following at most <see cref="DownloadPolicy.MaxRedirects"/> hops and re-checking every
    /// target against <see cref="DownloadPolicy.IsAllowedUrl"/>. A range, when given, is sent on every hop:
    /// the redirect target is where the bytes come from.
    /// </summary>
    public static async Task<HttpResponseMessage> GetCheckedAsync(HttpClient client, Uri uri, CancellationToken ct,
        long? rangeFrom = null, long? rangeTo = null)
    {
        if (!DownloadPolicy.IsAllowedUrl(uri)) throw new InvalidOperationException(Loc.T("Fetch.UrlRejected", uri));

        var current = uri;
        for (var hop = 0; hop < DownloadPolicy.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeFrom is not null) request.Headers.Range = new RangeHeaderValue(rangeFrom, rangeTo);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidOperationException(Loc.T("Fetch.RedirectNoTarget"));
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!DownloadPolicy.IsAllowedUrl(current)) throw new InvalidOperationException(Loc.T("Fetch.RedirectRejected", current));
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dispose before throwing: EnsureSuccessStatusCode would leak the response, and the
                // status code in the message tells the log which endpoint answered how.
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException(Loc.T("Fetch.HttpFailed", current, code));
            }

            return response;
        }

        throw new InvalidOperationException(Loc.T("Fetch.RedirectTooMany"));
    }

    /// <summary>Size of the whole file: from Content-Range for a partial answer, Content-Length otherwise.</summary>
    public static long? TotalLength(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

    /// <summary>
    /// Streams one address into a file. Nothing arriving for <see cref="StallTimeout"/> fails the
    /// transfer, so a source that has gone quiet hands over to the next one. A connection dropped
    /// part-way — including during a long pause — is continued with a ranged request from the byte it
    /// reached, up to <see cref="MaxResumes"/> times; a server that ignores the range is read again
    /// from the start rather than spliced.
    /// </summary>
    /// <param name="report">Bytes written, total size (-1 when unknown), smoothed rate.</param>
    /// <returns>The number of bytes written.</returns>
    public static async Task<long> TransferAsync(HttpClient client, Uri url, string target, long maxBytes,
        Action<long, long, double>? report, ModFetcher.DownloadGate? gate, CancellationToken ct)
    {
        long written = 0;
        long total = -1;
        var resumes = 0;
        var meter = new SpeedMeter();
        var buffer = new byte[81920];

        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                stall.CancelAfter(StallTimeout);
                using var response = await GetCheckedAsync(client, url, stall.Token, written > 0 ? written : null)
                    .ConfigureAwait(false);

                if (written > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        written = 0;
                        output.SetLength(0);
                        output.Position = 0;
                    }
                    else if (range?.From != written)
                    {
                        throw new InvalidOperationException(Loc.T("Fetch.RangeMismatch", url.Host));
                    }
                }

                total = TotalLength(response) ?? total;
                if (total > maxBytes) throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge", total / 1024 / 1024));

                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                var lastReport = Stopwatch.GetTimestamp();
                while (true)
                {
                    if (gate?.Paused == true)
                    {
                        stall.CancelAfter(Timeout.InfiniteTimeSpan);
                        report?.Invoke(written, total, 0);
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        stall.CancelAfter(StallTimeout);
                    }

                    var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    stall.CancelAfter(StallTimeout);

                    written += read;
                    if (written > maxBytes) throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge2"));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    if (report is not null && Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 150)
                    {
                        lastReport = Stopwatch.GetTimestamp();
                        report(written, total, meter.Report(written));
                    }
                }

                // A connection closed early ends the read like a finished one; only the length tells them apart.
                if (total > 0 && written < total) throw new IOException(Loc.T("Fetch.Truncated", written, total));

                report?.Invoke(written, total > 0 ? total : written, meter.Report(written));
                return written;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && stall.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.T("Fetch.Stalled", url.Host, (int)StallTimeout.TotalSeconds));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException &&
                                       !ct.IsCancellationRequested && written > 0 && resumes < MaxResumes)
            {
                resumes++;
                AppPaths.Log(Loc.T("Fetch.Resuming", url.Host, written, ex.Message));
            }
        }
    }

    /// <summary>
    /// Tracks a transfer rate over a half-second window, smoothed so the read-loop bursts do not make
    /// the displayed number jump.
    /// </summary>
    private sealed class SpeedMeter
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _lastBytes;
        private TimeSpan _lastTime;
        private double _bps;

        public double Report(long totalBytes)
        {
            var now = _watch.Elapsed;
            var dt = (now - _lastTime).TotalSeconds;
            if (dt >= 0.5)
            {
                var instant = (totalBytes - _lastBytes) / dt;
                _bps = _bps <= 0 ? instant : _bps * 0.4 + instant * 0.6;
                _lastBytes = totalBytes;
                _lastTime = now;
            }
            return _bps;
        }
    }
}
