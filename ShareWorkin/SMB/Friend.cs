using System;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

public sealed class Friend
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // This is the local nickname in this user's own notebook, not an account name.
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

    [JsonPropertyName("lastKnownAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastKnownAddress { get; set; }

    [JsonPropertyName("lastFoundAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastFoundAt { get; set; }

    [JsonPropertyName("lastCheckedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastCheckedAt { get; set; }

    // Host/share are connection facts behind an invited shop. They are not the
    // primary meaning of the friend list, which is "shops I was invited to".
    [JsonIgnore]
    public string UncPath => string.IsNullOrWhiteSpace(HostMachineName) || string.IsNullOrWhiteSpace(ShareName)
        ? string.Empty
        : $@"\\{HostMachineName}\{ShareName}";

    [JsonIgnore]
    public bool IsCurrentlyFound =>
        !string.IsNullOrWhiteSpace(LastFoundAt) &&
        string.Equals(LastFoundAt, LastCheckedAt, StringComparison.Ordinal);

    [JsonIgnore]
    public string ConnectHost => !IsCurrentlyFound || string.IsNullOrWhiteSpace(LastKnownAddress)
        ? HostMachineName
        : LastKnownAddress!;

    [JsonIgnore]
    public string ConnectUncPath => string.IsNullOrWhiteSpace(ConnectHost) || string.IsNullOrWhiteSpace(ShareName)
        ? string.Empty
        : $@"\\{ConnectHost}\{ShareName}";
}
