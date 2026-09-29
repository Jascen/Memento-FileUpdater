using System.Text.Json.Serialization;

namespace FileUpdaterClient.Updating;

//One entry of the server's file list: a path relative to the install folder, the file's MD5 hash and, from newer servers, its size
public class FileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = string.Empty;

    //Size in bytes. Null from servers that don't send it
    [JsonPropertyName("size")]
    public long? Size { get; set; }
}
