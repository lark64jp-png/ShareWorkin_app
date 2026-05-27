# 事実と推測

## 事実

- 本機 `friends.json` に東芝の `ownerCertThumbprint` が保存されている
- 本機 `notifycert.dat` は存在しない
- 本機ログに `Failed to persist TLS certificate: 指定された状態で使用するには無効なキーです。`
- 本機ログに `Out-of-sync detected`
- 本機ログに `HandleClientAsync error: The decryption operation failed`
- 東芝側更新履歴には、配置成功と交流通知送達失敗が同時に見える

## 推測

- 本機で通知用証明書保存に失敗し、起動ごとに自己署名証明書が再生成されている可能性が高い
- その結果、東芝側の保存済みサムプリントと一致せず、交流通知TLSだけ止まっている可能性が高い
- ユーザー一覧の `接続中` 判定は共有到達性と一部の証明書状態だけを見ており、交流通知経路の実利用時異常を先に反映できていない
