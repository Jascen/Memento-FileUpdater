using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileUpdaterPackages;

namespace FileUpdaterClient.Tests.Fakes;

//Serves a file list and file contents from memory, like the real update server
public class FakeServer : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new();
    public int FailuresBeforeSuccess { get; set; } //Fails this many file downloads first, to exercise retries
    public HttpStatusCode? ListStatus { get; set; }
    public string? RawList { get; set; }
    public bool IncludeSizes { get; set; } //Adds "size" to the file list, like newer servers
    public List<string> Downloads { get; } = new();
    public List<long?> RangeStarts { get; } = new(); //Start of each download's Range header, null when it asked for the whole file

    private readonly Dictionary<string, byte[]> _packageFiles = new();
    public byte[]? Manifest { get; set; } //Served as /packages/manifest.json, 404 when null
    public string? Signature { get; set; } //Served as /packages/manifest.sig, 404 when null
    public List<string> PackageDownloads { get; } = new();

    public void Add(string name, string content) => _files[name] = Encoding.UTF8.GetBytes(content);

    //Hosts packages the way a signed PackageSigner run would: zips plus a manifest signed with key
    public void PublishPackages(ECDsa key, params (string Role, string Version, string Rid, string Content)[] packages)
    {
        var entries = new List<PackageEntry>();
        foreach (var (role, version, rid, content) in packages)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var file = PackageFileName.Format(role, version, rid);
            _packageFiles[file] = bytes;
            entries.Add(new PackageEntry(role, version, rid, file, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length));
        }

        Manifest = new PackageManifest(DateTimeOffset.UtcNow, entries).ToJsonBytes();
        Signature = ManifestSigning.Sign(Manifest, key);
    }

    //Replaces a hosted package's bytes without updating the manifest, like a swapped file on the server
    public void TamperWithPackage(string role, string version, string rid, string content) =>
        _packageFiles[PackageFileName.Format(role, version, rid)] = Encoding.UTF8.GetBytes(content);

    public static string Md5(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private HttpResponseMessage ServePackage(string name)
    {
        if (name == PackageManifest.ManifestFileName)
            return Manifest == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Manifest) };
        if (name == PackageManifest.SignatureFileName)
            return Signature == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Signature) };

        var file = name.Replace("file/", string.Empty);
        PackageDownloads.Add(file);
        return _packageFiles.TryGetValue(file, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
        if (path.StartsWith("packages/")) return Task.FromResult(ServePackage(path["packages/".Length..]));
        if (path == string.Empty)
        {
            if (ListStatus != null) return Task.FromResult(new HttpResponseMessage(ListStatus.Value));
            var list = RawList ?? JsonSerializer.Serialize(_files.Select(f => new
            {
                name = f.Key,
                md5 = Convert.ToHexString(MD5.HashData(f.Value)).ToLowerInvariant(),
                size = IncludeSizes ? f.Value.Length : (long?)null,
            }), new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list) });
        }

        var name = path.Replace("file/", string.Empty).TrimStart('/');
        Downloads.Add(name);
        if (FailuresBeforeSuccess > 0)
        {
            FailuresBeforeSuccess--;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        var rangeStart = request.Headers.Range?.Ranges.First().From;
        RangeStarts.Add(rangeStart);
        if (!_files.TryGetValue(name, out var bytes))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        if (rangeStart == null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        if (rangeStart >= bytes.Length)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[(int)rangeStart.Value..]),
        });
    }
}
