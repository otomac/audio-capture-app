# Feature: 001-windows-audio-capture

Created: 2026-03-02
Status: Draft
Input: Windows上で動作するスタンドアローンの音声キャプチャアプリ。OSの音声デバイスを指定できるUIを持ち、選択した入力デバイスの音声をMP3ファイルに保存する。Windowsミキサー経由で動作し、デバイスを占有しない。C# / WPF で実装する。

---

## User Scenarios & Testing

### User Story 1 - 音声デバイスを選択して録音を開始する (Priority: P1)

録音を始める前に、どのマイク・デバイスを使うかを選びたい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: ドロップダウンリストに表示されたデバイス一覧から任意の入力デバイスを選択する
- Then: 選択したデバイスが録音対象として設定される

- Given: 録音対象デバイスを選択している
- When: 「録音開始」ボタンを押す
- Then: 録音が開始され、UIが録音中であることを示す（ボタン状態・インジケータ等）

**Edge Cases**
- 入力デバイスが1つも接続されていない場合 → 「デバイスが見つかりません」を表示しボタンを無効化
- アプリ起動後にデバイスが接続・切断された場合 → 「更新」ボタンで一覧を再取得できる

---

### User Story 2 - 録音を停止してMP3ファイルを保存する (Priority: P1)

録音したデータを任意のフォルダにMP3ファイルとして保存したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: 録音中である
- When: 「録音停止」ボタンを押す
- Then: 録音が停止し、指定の保存先フォルダに MP3 ファイルが出力される
- And: ファイル名は `YYYYMMDD_HHmmss.mp3` の形式で自動生成される

**Edge Cases**
- 保存先フォルダが存在しない場合 → フォルダを自動作成する
- ディスク残量不足の場合 → エラーメッセージを表示して録音を安全に停止する
- 録音時間が0秒（即停止）の場合 → ファイルを出力しない

---

### User Story 3 - 保存先フォルダを指定する (Priority: P2)

デフォルト保存先以外のフォルダに保存先を変更したい。

**Acceptance Scenarios (Given/When/Then)**

- Given: アプリを起動している
- When: 「保存先を選択」ボタンを押してフォルダダイアログでフォルダを選択する
- Then: 選択したフォルダが保存先として設定され、UIにパスが表示される
- And: 設定はアプリ再起動後も保持される

**Edge Cases**
- フォルダダイアログでキャンセルした場合 → 保存先は変更しない

---

### User Story 4 - 他アプリと同じデバイスを共有録音する (Priority: P1)

Zoom や Teams などが使用中のマイクであっても、同時に録音できる。

**Acceptance Scenarios (Given/When/Then)**

- Given: 他のアプリが同じ入力デバイスを使用している
- When: 録音を開始する
- Then: デバイスを占有せず（WASAPI Shared Mode）、双方が正常に動作する

**Edge Cases**
- 排他モードで他アプリがデバイスを占有している場合 → エラーメッセージを表示する

---

## Functional Requirements

- FR-001: Windows 10 以降で動作するスタンドアローンの .exe アプリとして配布できる
- FR-002: アプリ起動時に Windows の音声入力デバイス一覧をドロップダウンに表示する
- FR-003: ドロップダウンで選択したデバイスを録音対象にできる
- FR-004: WASAPI Shared Mode で録音する（デバイスを占有しない）
- FR-005: 録音データを MP3 形式（LAME エンコード）でファイルに保存する
- FR-006: ファイル名を `YYYYMMDD_HHmmss.mp3` 形式で自動生成する
- FR-007: 保存先フォルダをフォルダダイアログで指定できる
- FR-008: デフォルト保存先は `%USERPROFILE%\Documents\AudioCapture` とする
- FR-009: 保存先フォルダ設定をアプリ設定ファイル（JSON）に永続化する
- FR-010: 録音中であることを UI で視覚的に示す（ボタン状態の切り替えを含む）
- FR-011: デバイス一覧の手動更新（再スキャン）ができる
- FR-012: 録音中の経過時間をリアルタイムで表示する

---

## Key Entities

### AudioDevice
- `DeviceId` (string): WASAPI デバイス識別子
- `FriendlyName` (string): UI 表示用のデバイス名
- `IsDefault` (bool): デフォルトデバイスかどうか

### RecordingSession
- `FilePath` (string): 出力 MP3 のフルパス
- `StartedAt` (DateTime): 録音開始時刻
- `StoppedAt` (DateTime?): 録音停止時刻（録音中は null）
- `DeviceId` (string): 使用デバイスの識別子

### AppSettings
- `OutputFolder` (string): 保存先フォルダパス
- `LastSelectedDeviceId` (string?): 前回選択したデバイス ID

---

## Success Criteria

- SC-001: アプリ起動後 3 秒以内に入力デバイス一覧が表示される
- SC-002: 録音開始ボタン押下から 1 秒以内に実際の録音が始まる
- SC-003: 録音停止後、指定フォルダに有効な MP3 ファイルが存在する
- SC-004: 他アプリが同デバイスを使用中でも録音が開始できる（WASAPI Shared Mode 確認）
- SC-005: アプリ再起動後も保存先フォルダの設定が維持される
- SC-006: 1時間以上の録音で MP3 ファイルが破損せず再生できる
