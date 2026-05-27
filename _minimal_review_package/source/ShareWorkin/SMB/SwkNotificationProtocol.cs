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

        [JsonPropertyName("swkInstanceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SwkInstanceId { get; set; }

        [JsonPropertyName("issuedAt")]
        public required string IssuedAt { get; set; } // UTC ISO 8601
    }

    /// <summary>
    /// お友達側が送信：招待コードをください、という要求
    /// inviteId が空の場合は LAN 発見経由、
    /// inviteId が空でない場合は手動コード経由(店主側の InviteRegistry で照合)。
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
    /// 店主側が共有制限を変更したときに LAN 全体へブロードキャストする通知。
    /// 受信した友達側は即座に状態確認を行う。
    /// </summary>
    public sealed class SharePermissionChanged
    {
        [JsonPropertyName("type")]
        public string Type => "SharePermissionChanged";

        [JsonPropertyName("machineName")]
        public required string MachineName { get; set; }

        [JsonPropertyName("shareName")]
        public required string ShareName { get; set; }
    }

    /// <summary>
    /// 店主側がお店を閉じるときに LAN 全体へ事前通知するメッセージ。
    /// 受信した友達側は即座にキャッシュからそのお店を削除し、表示を更新する。
    /// </summary>
    public sealed class ShopClosing
    {
        [JsonPropertyName("type")]
        public string Type => "ShopClosing";

        [JsonPropertyName("shopMachineName")]
        public required string ShopMachineName { get; set; }

        [JsonPropertyName("shareName")]
        public required string ShareName { get; set; }

        [JsonPropertyName("issuedAt")]
        public required string IssuedAt { get; set; }
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

    public sealed class InteractionEventNotice
    {
        [JsonPropertyName("type")]
        public string Type => "InteractionEventNotice";

        [JsonPropertyName("eventId")]
        public required string EventId { get; set; }

        [JsonPropertyName("eventType")]
        public required string EventType { get; set; }

        [JsonPropertyName("senderMachineName")]
        public required string SenderMachineName { get; set; }

        [JsonPropertyName("senderDisplayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SenderDisplayName { get; set; }

        [JsonPropertyName("senderSwkInstanceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SenderSwkInstanceId { get; set; }

        [JsonPropertyName("senderShareName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SenderShareName { get; set; }

        [JsonPropertyName("receiverShareName")]
        public required string ReceiverShareName { get; set; }

        [JsonPropertyName("targetName")]
        public required string TargetName { get; set; }

        [JsonPropertyName("targetRelativePath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetRelativePath { get; set; }

        [JsonPropertyName("targetKind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetKind { get; set; }

        [JsonPropertyName("notificationType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NotificationType { get; set; }

        [JsonPropertyName("message")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Message { get; set; }

        [JsonPropertyName("issuedAt")]
        public required string IssuedAt { get; set; }
    }

    public sealed class InteractionEventResponse
    {
        [JsonPropertyName("type")]
        public string Type => "InteractionEventResponse";

        [JsonPropertyName("result")]
        public required string Result { get; set; }

        [JsonPropertyName("errorMessage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorMessage { get; set; }
    }
}
