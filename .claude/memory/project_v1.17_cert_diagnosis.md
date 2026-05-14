---
name: project-v1.17-cert-diagnosis
description: 2026-05-14 東芝再起動テストで判明した cert persistence 失敗と userlist-state.json 追加
metadata:
  type: project
---

## 今日確認したこと（2026-05-14）

SwkInstanceId 設計変更（認識ID）実装直後の最初の体系的テスト。東芝を再起動し、本器（Win11）とNECは待機。

### テスト結果
- 本器（Win11）: NEC・東芝ともに緑 → 正常
- NEC（Win10）: 東芝が2行表示（切替候補 + 証明書異常）
- 東芝（Win10、再起動後）: Win11が2行表示（切替候補 + 証明書異常）

### 根本原因
**全機種で `notifycert.dat` が生成されていない。**

ログに全台共通で確認:
```
[Warn] Failed to persist TLS certificate: 指定された状態で使用するには無効なキーです。
```

`TryPersistCertificate` の `certificate.Export(X509ContentType.Pfx)` が失敗している。
`RSA.Create(2048)` で生成した CNG キーがエクスポート不可のため。Win10/Win11 問わず全台で発生。

→ 毎回起動時に新しい証明書が生成される  
→ 再起動した相手の cert と friends.json に保存された thumbprint が不一致  
→ `LastAccessIssue = "cert-mismatch"` が friends.json に書き込まれる  
→ cert fix は意図通りに機能していない

### 今日追加した機能（コミット aa4cee6）
`userlist-state.json` をインストールフォルダーに保存する機能を追加。

`BuildUiFromCache` のたびに上書き。行ごとに以下を記録:
- Kind, StatusLabel, NameLabel, IpLabel, ShareFolderName
- friendRemoteSwkInstanceId（App ID）
- friendLastAccessIssue（cert-mismatch フラグ）
- friendOwnerCertThumbprint
- friendHostMachineName, friendShareName, friendLastKnownAddress
- shopSwkInstanceId, shopMachineName, shopIpAddress, shopShareName
- candidateHostName, candidateAddress

診断ツールとしての位置づけ。cert-mismatch フラグや ID 照合の成否をログなしで確認できる。

### インストーラー
`ShareWorkin_v1.17_install.exe` 生成済み（バージョン据え置き）。

### 次のアクション
- 東芝・NEC に新インストーラーを入れて `userlist-state.json` を確認
- cert persistence の修正: `CreateSelfSignedCertificate` で RSA キーをエクスポート可能に生成する方法を Codex と相談
  - 修正候補: `RSACryptoServiceProvider` への変更、または CNG の ExportPolicy 設定
- `SwkNotificationBroadcaster.cs` の `LoadOrCreateCertificate` / `TryPersistCertificate` が対象
