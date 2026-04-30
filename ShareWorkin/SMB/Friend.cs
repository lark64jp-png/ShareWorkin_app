using System;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class Friend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("host")]
    public string HostMachineName { get; set; } = string.Empty;

    [JsonPropertyName("share")]
    public string ShareName { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public string UserName { get; set; } = "swkguest";

    [JsonPropertyName("passProtected")]
    public string PasswordProtected { get; set; } = string.Empty;

    [JsonPropertyName("access")]
    public string AccessLevel { get; set; } = "Full";

    [JsonPropertyName("label")]
    public string ProfileLabel { get; set; } = string.Empty;

    [JsonPropertyName("addedAt")]
    public string AddedAt { get; set; } = string.Empty;

    [JsonPropertyName("lastSeenAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastSeenAt { get; set; }

    [JsonIgnore]
    public string UncPath => string.IsNullOrWhiteSpace(HostMachineName) || string.IsNullOrWhiteSpace(ShareName)
        ? string.Empty
        : $@"\\{HostMachineName}\{ShareName}";
}
