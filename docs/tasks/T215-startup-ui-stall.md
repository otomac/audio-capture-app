# T215 — 起動時に UI スレッドが約 2.4 秒止まる原因を調べる

> **状態:** 完了 — 2026-11-24
> **台帳:** [docs/tasks/backlog.md](./backlog.md)

## 1. 目的

T212 の同一プロセスの検証で、起動直後〜ファイル文字起こしのダイアログを開くまでの間に、100ms のタイマーが 2.3〜2.6 秒間隔を空けた。
どこで止まっているかを突き止め、小さく直せるなら直す。

## 2. 調べたこと

`C:\personal\dev\audio-capture-work\t215-startupcheck\`（本体を同一プロセスで起動し、100ms のタイマーが 200ms 超空いた区間を起動の各段階と並べる）と
`t215-enumcheck\`（デバイス列挙だけを測る）。

**止まっていたのは `new MainWindow()` の中で、ウィンドウを出す前だった。** Whisper のモデル読み込みは関係ない。

| 設定 | UI が止まった長さ | `new MainWindow()` |
|---|---|---|
| そのまま（モデル読み込みあり・GPU） | 2,557 ms | 1,999 ms |
| Whisper モデルなし | 2,345 ms | 1,777 ms |

`MainViewModel` の生成の中身を順に動かして測ると、**デバイスの列挙（`AudioCaptureService.RefreshDevices`）が 1,720〜1,763 ms** を占めた。
NAudio の生成・設定の読み込み・マイクの監視の開始は 0〜10 ms だった。2 回目の列挙も同じだけかかるので、初回の JIT ではない。

列挙のうち遅いのは、**デバイスの表示名（NAudio の `MMDevice.FriendlyName`）を読むところ**だった。1 台 50〜520 ms かかる。

- NAudio の `FriendlyName` は、プロパティのキー（1 台に 166〜185 個）を、「あるか」（`Contains`）と「値」（インデクサー）の 2 回なめる。
  しかも 1 キーごとに件数（`Count` → `GetCount`）も取り直す。キーで直接読む `IPropertyStore.GetValue` は NAudio の内部（`internal`）にあって使えない。
- この PC はスピーカー側のデバイスが 5 台ある（ディスプレイの音声 3 台を含む）。

ユーザーから見ると、これは「出ているウィンドウが固まる」ではなく、**起動してからウィンドウが出るまでが約 2 秒遅い**、という形で現れる。
もう 1 か所、メインウィンドウの「更新」（REQ-DEV-01）を押したときも、同じ列挙で同じだけ固まる（未計測、コードから）。

## 3. 決定事項

| # | 決定 | 結論 |
|---|---|---|
| D1 | 直し方 | **キーを 1 回だけなめ、件数も 1 回だけ取り、見つけた位置の値を読む。** NAudio の公開 API（`PropertyStore.Count` / `Get(i)` / `GetValue(i)`）だけで書ける。独自の COM 定義は足さない（NAudio を直接使う方針。CLAUDE.md） |
| D2 | 列挙を別スレッドへ移すか | **移さない。** 移すと、ウィンドウが出た後でデバイス一覧が埋まり、前回の選択の復元やモニターの開始が後ろへずれる。順序の変更が大きく、D1 で足りる |
| D3 | 名前が無いとき | NAudio と同じ `"Unknown"` を返す（表示を変えない） |

## 4. 仕様書への影響

- [x] 変更なし（仕様非影響。列挙の結果・順序・表示は変わらない。読む手順だけを変える）

## 5. アーキテクチャへの影響

- ADR: 不要（Service の中の読み方だけ。NAudio を直接使ったまま）

## 6. 変更ファイル一覧

| ファイル | 変更内容 |
|---|---|
| `AudioCaptureApp/Services/AudioCaptureService.cs` | `EnumerateDevices` の表示名を `ReadFriendlyName`（キーを 1 回だけなめる）で読む。`IndexOfKey` |
| `AudioCaptureApp.Tests/AudioCaptureServiceTests.cs` | テスト |

## 7. テスト一覧

- **`IndexOfKey_Found_ReturnsIndexAndStopsScanning`** — 見つけたら、それより後ろのキーは読まない
- **`IndexOfKey_NotFound_ReturnsMinusOneAfterReadingEachKeyOnce`** — 無ければ -1。各キーは 1 回ずつしか読まない
- **`IndexOfKey_MatchesBothFormatIdAndPropertyId`** — 同じ GUID でも番号が違えばほかのキーとみなす

## 8. 確認

**速さ**（`t215-startupcheck`、本体を同一プロセスで起動。直す前は 2〜3 回、直した後は 3 回）:

| | 直す前 | 直した後 |
|---|---|---|
| `RefreshDevices()`（マイク 2 台・スピーカー 5 台） | 1,720〜1,763 ms | **490 ms** |
| `new MainWindow()` | 1,777〜1,999 ms | **554〜775 ms** |

表示名の読み方だけを新しいプロセスで比べると、7 台で 2,279〜2,520 ms が 634〜744 ms になった（`t215-enumcheck`）。

**同じ値が返るか**（`t215-enumcheck`）: この PC の全エンドポイント 28 台（有効 7 台・無効・抜かれている・存在しないものを含む）で、
本体の読み方と NAudio の `FriendlyName` の結果が**全部一致した**。存在しないデバイスのうち 3 台は、表示名の値を読むところで
どちらも同じ例外（`COMException 0xE000020B`）を出す。本体は有効なデバイスだけを列挙するので、この例外には当たらない（直す前と同じ）。

> **残り:** 表示名を 1 台読むのに、まだ 20〜130 ms かかる（キーを 1 つずつ読むため）。これ以上縮めるには、キーで直接読む
> `IPropertyStore.GetValue` を自前の COM 定義で呼ぶか、列挙を別スレッドへ移す必要があり、どちらも D1・D2 で見送った。
> 起動直後〜ダイアログを開くまでの検証では、`new MainWindow()` の直前にも 0.5 秒ほどの空きが出る。直す前は上の停止とひと続きに
> 見えていた。検証プログラムが本体の型を初めて読み込む時間だと見ているが、未確認である。

---

## 実行結果 (2026-11-24)

- `dotnet build` : 警告 0 件 / エラー 0 件
- `dotnet format`: 差分なし
- `dotnet test`  : 567 件成功 / 0 件失敗 / 0 件スキップ（3 件追加）
- 計画からの逸脱: なし
