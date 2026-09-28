using System.Text.Json.Serialization;

namespace FileUpdaterClient.Updating;

//One entry of the server's file list: a path relative to the install folder and the file's MD5 hash
public class FileEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("md5")]
    public string Md5 { get; set; } = string.Empty;
}
