using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;

namespace DLSSGManager.Tests;

/// <summary>Scripted HTTP responses, so the transfer logic is tested without a network.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request, Requests.Count));
    }
}

/// <summary>A stream that delivers some bytes, then fails or stops answering.</summary>
internal sealed class FlakyStream(byte[] data, int failAfter, bool hang) : Stream
{
    private int _position;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= failAfter && failAfter < data.Length)
        {
            if (hang) { await Task.Delay(Timeout.Infinite, ct); }
            throw new IOException("connection reset");
        }
        var n = Math.Min(buffer.Length, Math.Min(data.Length, failAfter) - _position);
        data.AsMemory(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class DownloadPolicyTests
{
    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://raw.githubusercontent.com/x", true)]
    [InlineData("https://netix.dl.sourceforge.net/x", true)]
    [InlineData("http://github.com/x", false)]                    // not HTTPS
    [InlineData("https://evil.example/x", false)]                  // not allow-listed
    [InlineData("https://github.com.evil.example/x", false)]       // suffix trick
    [InlineData("https://dl.sourceforge.net.evil/x", false)]
    [InlineData("https://140.82.112.3/x", false)]                  // literal address
    public void Urls_are_checked_against_the_allow_list(string url, bool allowed) =>
        Assert.Equal(allowed, DownloadPolicy.IsAllowedUrl(new Uri(url)));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("169.254.169.254", false)]   // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("140.82.112.3", true)]
    [InlineData("2606:50c0:8000::154", true)]
    public void Only_public_addresses_are_connectable(string address, bool expected) =>
        Assert.Equal(expected, DownloadPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void Allowed_host_resolving_to_private_addresses_gets_no_targets()
    {
        var targets = DownloadPolicy.ConnectTargets(new DnsEndPoint("github.com", 443),
            new[] { IPAddress.Parse("127.0.0.1"), IPAddress.Parse("10.0.0.5") }, proxy: null);
        Assert.Empty(targets);
    }

    [Fact]
    public void Only_the_public_addresses_of_a_mixed_answer_are_used()
    {
        var targets = DownloadPolicy.ConnectTargets(new DnsEndPoint("github.com", 443),
            new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("140.82.112.3") }, proxy: null);
        Assert.Equal(new[] { IPAddress.Parse("140.82.112.3") }, targets);
    }

    [Fact]
    public void Configured_local_proxy_is_reachable()
    {
        var targets = DownloadPolicy.ConnectTargets(new DnsEndPoint("127.0.0.1", 7897),
            new[] { IPAddress.Loopback }, proxy: new Uri("http://127.0.0.1:7897"));
        Assert.Equal(new[] { IPAddress.Loopback }, targets);
    }

    [Fact]
    public void A_local_endpoint_that_is_not_the_proxy_is_refused()
    {
        var targets = DownloadPolicy.ConnectTargets(new DnsEndPoint("127.0.0.1", 8080),
            new[] { IPAddress.Loopback }, proxy: new Uri("http://127.0.0.1:7897"));
        Assert.Empty(targets);
    }
}

[Collection("PayloadHttp")]
public class HttpTransferTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TimeSpan _stall = HttpTransfer.StallTimeout;

    public void Dispose()
    {
        PayloadHttp.TestHandler = null;
        HttpTransfer.StallTimeout = _stall;
        _dir.Dispose();
    }

    private static HttpResponseMessage Ok(byte[] body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static readonly Uri Github = new("https://github.com/sdli1995/dlssg_for_sm86/file.dll");

    [Fact]
    public async Task Redirect_to_an_allowed_host_is_followed()
    {
        var body = DeploymentFixture.Random(1000);
        var handler = new ScriptedHandler((_, n) => n == 1 ? Redirect("https://objects.githubusercontent.com/f") : Ok(body));
        using var client = new HttpClient(handler);
        var target = _dir.File("a.bin");

        var written = await HttpTransfer.TransferAsync(client, Github, target, 10_000, null, null, CancellationToken.None);

        Assert.Equal(1000, written);
        Assert.Equal(body, File.ReadAllBytes(target));
        Assert.Equal("objects.githubusercontent.com", handler.Requests[1].RequestUri!.Host);
    }

    [Theory]
    [InlineData("https://evil.example/payload.dll")]
    [InlineData("http://objects.githubusercontent.com/f")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task Redirect_off_the_allow_list_is_refused(string location)
    {
        var handler = new ScriptedHandler((_, _) => Redirect(location));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpTransfer.TransferAsync(client, Github, _dir.File("b.bin"), 10_000, null, null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Redirect_loop_is_bounded()
    {
        var handler = new ScriptedHandler((_, _) => Redirect("https://github.com/again"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpTransfer.TransferAsync(client, Github, _dir.File("c.bin"), 10_000, null, null, CancellationToken.None));
        Assert.Equal(DownloadPolicy.MaxRedirects, handler.Requests.Count);
    }

    [Fact]
    public async Task Body_larger_than_the_cap_is_refused()
    {
        var handler = new ScriptedHandler((_, _) => Ok(new byte[5000]));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpTransfer.TransferAsync(client, Github, _dir.File("d.bin"), 4000, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Dropped_connection_resumes_with_a_range_request()
    {
        var body = DeploymentFixture.Random(200_000);
        var handler = new ScriptedHandler((request, n) =>
        {
            if (n == 1)
            {
                var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlakyStream(body, 50_000, hang: false)) };
                first.Content.Headers.ContentLength = body.Length;
                return first;
            }
            var from = (int)request.Headers.Range!.Ranges.First().From!.Value;
            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[from..]) };
            rest.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, body.Length - 1, body.Length);
            return rest;
        });
        using var client = new HttpClient(handler);
        var target = _dir.File("e.bin");

        await HttpTransfer.TransferAsync(client, Github, target, 1_000_000, null, null, CancellationToken.None);

        Assert.Equal(body, File.ReadAllBytes(target));
        Assert.Equal(50_000, handler.Requests[1].Headers.Range!.Ranges.First().From);
    }

    [Fact]
    public async Task Server_ignoring_the_range_is_read_again_from_the_start()
    {
        var body = DeploymentFixture.Random(100_000);
        var handler = new ScriptedHandler((_, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlakyStream(body, 30_000, hang: false)) }
            : Ok(body));
        using var client = new HttpClient(handler);
        var target = _dir.File("f.bin");

        await HttpTransfer.TransferAsync(client, Github, target, 1_000_000, null, null, CancellationToken.None);

        Assert.Equal(body, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Silent_source_times_out()
    {
        HttpTransfer.StallTimeout = TimeSpan.FromMilliseconds(300);
        var body = DeploymentFixture.Random(10_000);
        var handler = new ScriptedHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlakyStream(body, 0, hang: true)) });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            HttpTransfer.TransferAsync(client, Github, _dir.File("g.bin"), 1_000_000, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Pause_holds_the_transfer_until_resumed()
    {
        var body = DeploymentFixture.Random(300_000);
        var handler = new ScriptedHandler((_, _) => Ok(body));
        using var client = new HttpClient(handler);
        var gate = new ModFetcher.DownloadGate();
        gate.Pause();

        var transfer = HttpTransfer.TransferAsync(client, Github, _dir.File("h.bin"), 1_000_000, null, gate, CancellationToken.None);
        await Task.Delay(200);
        Assert.False(transfer.IsCompleted);

        gate.Resume();
        Assert.Equal(body.Length, await transfer.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

public class ArchiveExtractorTests
{
    private static string Zip(TempDir dir, params (string Name, string Text)[] entries)
    {
        var path = dir.File("archive.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        return path;
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("version.dll:hidden")]
    public void Entries_escaping_staging_are_refused(string name)
    {
        using var dir = new TempDir();
        var zip = Zip(dir, ("dlssg_sm86.ini", "x"), (name, "payload"));
        var staging = dir.File("staging");

        Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.Extract(zip, staging, CancellationToken.None));
        Assert.False(File.Exists(dir.File("evil.dll")));
    }

    [Fact]
    public void Single_wrapper_folder_is_flattened()
    {
        using var dir = new TempDir();
        var zip = Zip(dir, ("dlssg-release-0.4.1/dlssg_sm86.ini", "ini"), ("dlssg-release-0.4.1/version.dll", "dll"),
            ("__MACOSX/._version.dll", "junk"));
        var staging = dir.File("staging");

        ArchiveExtractor.Extract(zip, staging, CancellationToken.None);

        Assert.Equal("ini", File.ReadAllText(Path.Combine(staging, "dlssg_sm86.ini")));
        Assert.True(File.Exists(Path.Combine(staging, "version.dll")));
    }

    [Fact]
    public void Archive_without_a_payload_is_refused()
    {
        using var dir = new TempDir();
        var zip = Zip(dir, ("readme.txt", "nothing here"));
        Assert.Throws<InvalidOperationException>(() => ArchiveExtractor.Extract(zip, dir.File("staging"), CancellationToken.None));
    }
}

[Collection("PayloadHttp")]
public class DownloadEndToEndTests : IDisposable
{
    public void Dispose() => PayloadHttp.TestHandler = null;

    [Fact]
    public async Task Mirror_serving_altered_bytes_installs_nothing()
    {
        // Every request answers with the wrong bytes; the manifest check must reject the payload and the
        // mod root must stay empty.
        PayloadHttp.TestHandler = () => new ScriptedHandler((request, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[16]) });
        using var root = new TempDir();

        var result = await ModFetcher.DownloadIntoAsync(root.Path, null, CancellationToken.None, sourceId: "jsdelivr");

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(Path.Combine(root.Path, ModStore.VersionsDirName)) &&
                     Directory.EnumerateDirectories(Path.Combine(root.Path, ModStore.VersionsDirName)).Any(d => !Path.GetFileName(d).StartsWith('.')));
        Assert.Null(ModStore.ActiveVersion(root.Path));
    }

    [Fact]
    public async Task Exact_release_from_any_source_installs_as_the_active_version()
    {
        var repo = LocalRelease();
        if (repo is null) return; // needs the real 0.4.1 files in mod/; CI skips
        PayloadHttp.TestHandler = () => new ScriptedHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var file = PayloadCatalog.Supported.Files.FirstOrDefault(f => path.EndsWith("/" + f.Source, StringComparison.Ordinal));
            if (file is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var bytes = File.ReadAllBytes(Path.Combine(repo, file.Destination.Replace('/', Path.DirectorySeparatorChar)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var root = new TempDir();

        var result = await ModFetcher.DownloadIntoAsync(root.Path, null, CancellationToken.None, sourceId: "ghproxy");

        Assert.True(result.Ok, result.Message + "\n" + string.Join("\n", result.Lines));
        Assert.Equal(ModFetcher.SupportedRelease, ModStore.ActiveVersion(root.Path));
        Assert.True(new ModSource(ModStore.ActiveDirectory(root.Path)).IsValid);
    }

    private static string? LocalRelease()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DLSSGManager.sln"))) dir = dir.Parent;
        if (dir is null) return null;
        var active = ModStore.ActiveDirectory(Path.Combine(dir.FullName, "mod"));
        return PayloadVerifier.CheckInstalled(active, PayloadCatalog.Supported) is null && File.Exists(Path.Combine(active, "version.dll"))
            ? active : null;
    }
}

[CollectionDefinition("PayloadHttp", DisableParallelization = true)]
public class PayloadHttpCollection;
