# UI～Trayを利用しないもの

作成日: 2026-06-17

## 目的

今回の区切り時点で、`MainWindow` 側に残っている「Tray を経由せず UI アプリ側で直接持っている処理」を整理する。

## 結論

ローカル共有状態確認については、`GET_SHARE_SNAPSHOT` により `UI -> Tray -> Windows正本` の経路へ寄せられている。  
ただし、UI アプリ全体としては、僚機向けの問い合わせや相手共有探索処理はまだ `MainWindow.xaml.cs` 側に残っている。

## Tray を使わず UI 側に残っている主な処理

### 1. 相手共有の探索・再接続

- `ProbeActiveFriendShopAsync(Friend friend)`
- `ResolveLiveFriendShopWithRetryAsync(...)`
- `NavigateToFriendShopAsync(...)`
- `TryFindAccessibleFriendPathAsync(...)`

これらは、相手共有の生存確認、接続先更新、UNC 到達確認を UI 側で実施している。

### 2. 相手共有の定期確認

- `FriendShopPollTimer_Tick(...)`
- `RunFriendShopPollAsync()`

相手共有表示中の再確認は、現在も UI 側タイマーと UI 側ロジックで回している。

### 3. 相手通知の待受

- `StartFriendShopPermissionListener(...)`
- `UdpClient.ReceiveAsync(...)`

権限変更通知の受信トリガーも、現状は UI 側で待ち受けている。

### 4. ネットワークキャッシュ参照

- `FindLiveShopInfo(...)`
- `SwkNetworkCache.RefreshAsync(...)`
- `SwkNetworkCache.ShopInfos` 参照

僚機候補や相手共有情報の探索は、Tray 経由へ完全移譲されていない。

## Tray 経由へ寄せ済みのもの

- ローカル共有状態確認
- `GET_SHARE_SNAPSHOT`
- Windows 正本取得
- stale / fallback / timeout / failed の UI 共通状態バー反映

## このメモの位置づけ

このファイルは、「今回の実装でどこまで Tray 経由化できたか」と「まだ UI 直持ちのまま残っている範囲」を切り分けるための区切りメモ。

次に再開する場合は、`FriendShop` 系の探索・ポーリング・通知待受を Tray 側へ寄せるかどうかを別テーマとして整理する。
