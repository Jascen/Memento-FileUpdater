using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileUpdaterClient.Tests.Fakes;

//Serves a file list and file contents from memory, like the real update server
public class FakeServer : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _files = new();
    public int FailuresBeforeSuccess { get; set; } //Fails this many file downloads first, to exercise retries
    public HttpStatusCode? ListStatus { get; set; }
    public string? RawList { get; set; }
    public List<string> Downloads { get; } = new();

    public void Add(string name, string content) => _files[name] = Encoding.UTF8.GetBytes(content);

    public static string Md5(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
        if (path == string.Empty)
        {
            if (ListStatus != null) return Task.FromResult(new HttpResponseMessage(ListStatus.Value));
            var list = RawList ?? JsonSerializer.Serialize(_files.Select(f => new
            {
                name = f.Key,
                md5 = Convert.ToHexString(MD5.HashData(f.Value)).ToLowerInvariant(),
            }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list) });
        }

        var name = path.Replace("file/", string.Empty).TrimStart('/');
        Downloads.Add(name);
        if (FailuresBeforeSuccess > 0)
        {
            FailuresBeforeSuccess--;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }

        return Task.FromResult(_files.TryGetValue(name, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
