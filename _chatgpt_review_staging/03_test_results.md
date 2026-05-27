# 実機テスト結果メモ

確認済み事実:
- 本機でファイル受信自体は成功
- 本機の通知表示は出る
- 通知履歴は追加される
- 本機ログには `Out-of-sync detected` と `HandleClientAsync error: The decryption operation failed` が残る
- 本機ログには `Failed to persist TLS certificate` が残る
- 本機の `friends.json` には東芝の `ownerCertThumbprint` が保存されている
- 本機の `notifycert.dat` は存在しない
- 東芝側更新履歴では、配置成功と交流通知送達失敗が同時に見える

ユーザー報告:
- 交流アプリとして、コピー成功相手を不審扱いする見え方は不適切
- Tray は現在交流できる相手を先に確認すべき
