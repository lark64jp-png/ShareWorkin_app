using System;
using System.Text.Json.Serialization;

namespace ShareWorkin.SMB;

/// <summary>
/// ShareWorkin 通知プロトコル（JSON over TLS/TCP）
/// 店主とお友達がアプリ同士で対話するための約束
/// </summary>
public static class SwkNotificationProtocol
{
    public const string ContentType = "application/json";

    /// <summary>
    /// 店主側が定期的に送信：「ここにいます」という通知
    /// </summary>
    public sealed class ShopNotification
    {
        [JsonPropertyName("type")]
        public string Type => "ShopNotification";

        [JsonPropertyName("shopMachineName")]
        public required string ShopMachineName { get; set; }

        [JsonPropertyName("shareName")]
        public required string ShareName { get; set; }

        [JsonPropertyName("listeningPort")]
        public required int ListeningPort { get; set; }

        [JsonPropertyName("issuedAt")]
        public required string IssuedAt { get; set; } // UTC ISO 8601
    }

    /// <summary>
    /// お友達側が送信：招待コードをください、という要求
    /// inviteId が空の場合は LAN 発見経由(その場で店主の承認を求める)、
    /// inviteId が空でない場合は手動コード経由(店主側の InviteRegistry で照合 + 承認)。
    /// </summary>
    public sealed class InviteCodeRequest
    {
        [JsonPropertyName("type")]
        public string Type => "InviteCodeRequest";

        [JsonPropertyName("shareName")]
        public required string ShareName { get; set; }

        [JsonPropertyName("clientMachineName")]
        public required string ClientMachineName { get; set; }

        [JsonPropertyName("inviteId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? InviteId { get; set; }
    }

    /// <summary>
    /// 店主側が応答：パスワードをお友達に渡す
    /// result が "Ok" の場合のみ password を含める
    /// </summary>
    public sealed class InviteCodeResponse
    {
        [JsonPropertyName("type")]
        public string Type => "InviteCodeResponse";

        [JsonPropertyName("result")]
        public required string Result { get; set; } // "Ok" / "ShopClosed" / "NotFound" / "Denied" / "InviteIdInvalid" / "InviteIdUsed"

        [JsonPropertyName("password")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Password { get; set; }

        [JsonPropertyName("errorMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 店主側がお友達の登録を確認し、逆方向に通知を送る
    /// </summary>
    public sealed class VisitorNotification
    {
        [JsonPropertyName("type")]
        public string Type => "VisitorNotification";

        [JsonPropertyName("visitorMachineName")]
        public required string VisitorMachineName { get; set; }

        [JsonPropertyName("visitorDisplayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? VisitorDisplayName { get; set; }

        [JsonPropertyName("arrivedAt")]
        public required string ArrivedAt { get; set; } // UTC ISO 8601
    }
}
