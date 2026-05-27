# 処理経路表

## 表示判定

- ユーザー一覧の `登録済み / 接続中`
  - `UserListWindow.BuildRowsFromCache`
  - 条件: `liveShop is not null` かつ `!friend.HasCertificateMismatch`
  - さらに `IsConnectedFriend(...)` なら `接続中`
  - そうでなければ `再開待ち`

## 入力受付判定

- 相手への操作前確認
  - `MainWindow.TryConfirmInteractionAction`
  - 友達ショップ表示中のみ通知メッセージ入力ダイアログを開く

## 実行可否判定

- 相手に対する通知送信
  - `MainWindow.SendConfirmedInteractionToFriendAsync`
  - 送信先 `Friend.OwnerCertThumbprint` を `SwkNotificationListener.SendInteractionEventAsync` へ渡す

## 実行直前検証

- 交流通知TLS
  - `SwkNotificationListener.SendInteractionEventAsync`
  - サーバー証明書サムプリントと `expectedThumbprint` を比較
  - 不一致で `AuthenticationException`

## 実行後の警告・キャンセル処理

- 招待コード取得系の証明書不一致
  - `TryRefreshFriendPasswordAsync`
  - `PersistFriendAccessIssue(friend, cert-mismatch)` を実行
- 交流通知送信失敗
  - 今回の修正で `SendConfirmedInteractionToFriendAsync` でも `cert-mismatch` を `LastAccessIssue` に反映
- 正規交流通知を受けられない場合
  - `TryRegisterExternalReceive` から `OutOfSyncDetected`
  - 監視受信として通知履歴へ載る
