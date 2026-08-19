# 20 — アーキテクチャ規範

**アーキテクチャ優先（法 3）の本体。** ここに書かれた構造は、実装の都合で曲げてはならない。
変更が必要なら実装前に ADR（`docs/adr/`）を書く。

現行構造の記述は `docs/spec/02_architecture.md`（**現状の正**）にある。
本書はそこに書かれた構造を **守らせるためのルール** であり、現状記述と規範を分離している。

---

## 1. レイヤーと依存方向

```
View  ──→  ViewModel  ──→  Service  ──→  外部ライブラリ / OS
  │            │              │
  └────────────┴──────────────┴──────→  Model
```

| 層 | 場所 | 責務 | 禁止 |
|---|---|---|---|
| **View** | `MainWindow.xaml(.cs)` / `Controls/` | 表示とユーザー操作の受付、バインディング | 業務ロジック、Service の直接呼び出し |
| **ViewModel** | `ViewModels/MainViewModel.cs` | UI 状態の保持、コマンド、Service のオーケストレーション | NAudio / Whisper.net / `System.IO` の直接使用 |
| **Service** | `Services/` | 外部リソース（NAudio・Whisper.net・ファイル I/O）の操作 | View / ViewModel への参照、`MessageBox` 等の UI 呼び出し |
| **Model** | `Models/` | データ保持のみの POCO | ロジック、外部依存 |

### 破ってはいけない依存の向き

1. **Service → ViewModel の参照は禁止。** Service から上位へ伝えたいことは **イベント** で通知する
   （`RecordingError` / `Error` / `RuntimeInfo` が既存の例）。
2. **Model → 何か への参照は禁止。** Model は他の層も外部ライブラリも知らない。
3. **View → Service の直接呼び出しは禁止。** 必ず ViewModel を経由する。
4. **Service 間の相互参照は禁止。** 現状 `AudioCaptureService → TranscriptionService` の
   **一方向** のみ許可されており、`SetTranscriptionService` による null 許容の注入で疎結合を保っている。
   逆向き（`TranscriptionService → AudioCaptureService`）を作ってはならない。

## 2. 意図的にやっていないこと（変えるなら ADR）

以下は「まだやっていない」のではなく **意図して採用していない**。よかれと思って導入しないこと。

| 項目 | 現状 | 理由 |
|---|---|---|
| **DI コンテナ** | 不使用。`MainWindow` が `MainViewModel` を直接 `new` し、ViewModel が Service を直接生成する | 単一ウィンドウ・単一 ViewModel の規模では、コンテナの間接性が理解のコストに見合わない |
| **ViewModel の分割** | `MainViewModel.cs` 1 ファイルに集約 | シンプル優先（`CLAUDE.md` の方針）。分割の閾値は §6 参照 |
| **Service のインターフェース抽象** | 具象クラスを直接使用。NAudio も独自ラップせず直接使う | 実装が 1 つしかない抽象は害。テスト容易性は `InternalsVisibleTo` ＋ 純粋関数の切り出しで確保する |
| **リポジトリ／永続化層** | `SettingsService` が直接 JSON を読み書き | 永続化対象が設定ファイル 1 つのみ |

## 3. スレッドモデルの規範

本アプリは UI スレッドのほかに、NAudio のコールバックスレッド、`WriterLoop` / `TranscriptionLoop`
専用スレッド、OS のボリューム通知コールバック、`Task.Run` ワーカーが並走する
（実体は `docs/spec/02_architecture.md` §4）。

**絶対規則：**

1. **UI スレッド以外からバインド対象のプロパティを更新してはならない。**
   必ず `Application.Current.Dispatcher.BeginInvoke` を経由する。
   これは `CLAUDE.md` の開発ルールであり、ハーネスの強制対象でもある。
2. **オーディオコールバック（`DataAvailable`）の中で重い処理・ブロッキング・ロック待ちをしない。**
   バッファへ積むところまでに留め、加工は専用スレッドで行う。
3. **共有バッファへのアクセスは同期を明示する。** 「たまたま動いている」を許容しない。
4. **`async void` はイベントハンドラーのみ。** それ以外は `Task` を返す。
5. **`.Result` / `.Wait()` による同期待ちをしない。** UI スレッドではデッドロックする。

## 4. エラーハンドリングの規範

`docs/spec/02_architecture.md` §7 の方針を規範として固定する。

- **Service 層** — 例外を握り潰さない。`InvalidOperationException` で再送出するか、
  `Error` / `RecordingError` イベントで非同期に通知する。
- **ViewModel 層** — Service からの例外・イベントを受け、`StatusMessage` としてユーザー向けの
  日本語メッセージに変換する。ここが例外の終着点である。
- **機能縮退の例外** — デバイス固有の機能制限（`AudioEndpointVolume` 非対応等）は catch して
  機能を落として継続してよい。ただし **縮退したことをユーザーに伝える**（黙って落とさない）。
- **`catch (Exception)` は既定では禁止**（アナライザー CA1031）。上記 3 種のいずれかに該当する箇所では、
  `#pragma warning disable CA1031` と **理由 1 行** を添えて局所的に許可する。

## 5. 新しいコードをどこに置くか

| 追加したいもの | 置き場所 | 補足 |
|---|---|---|
| 画面に出す値・状態 | `MainViewModel` のプロパティ | `[ObservableProperty]` を使う |
| ボタン等の操作 | `MainViewModel` のコマンド | `[RelayCommand]` を使う |
| 外部リソースを触る処理 | 既存 Service のメソッド | 3 つのどれにも属さないなら新規 Service を検討（ADR 対象） |
| 副作用のない計算 | Service の `internal static` メソッド | テスト対象にする（`BytesToFloats` / `CalculatePeak` / `SplitVoicedRegions` が既存の例） |
| データの入れ物 | `Models/` の POCO | ロジックを入れない |
| 再利用する UI 部品 | `Controls/` のユーザーコントロール | 依存プロパティで ViewModel とバインドする |

## 6. 構造が壊れかけているサイン

以下に該当したら、その場で直さず **ADR を起票して構造変更を提案** する。

- `MainViewModel.cs` が **1,500 行** を超えた → 機能単位の ViewModel 分割を検討
- 1 つの Service が **3 つ以上の無関係な外部リソース** を触っている → Service 分割を検討
- View のコードビハインドに `if` による業務判断が現れた → ViewModel へ移す
- テストを書くために `public` にした（本来 `private` でよい）メンバーが増えた
  → `InternalsVisibleTo` ＋ `internal` で足りるはず。純粋関数として切り出せないか見直す

## 7. テスト容易性の設計規範

DI コンテナもインターフェース抽象も使わないため、テスト容易性は **設計側で確保する**。

- 副作用（I/O・デバイス操作）と計算を分離し、**計算部分を `internal static` の純粋関数** にする。
- `AudioCaptureApp` は `AudioCaptureApp.Tests` に `InternalsVisibleTo` を与えている
  （[AudioCaptureApp.csproj](../../AudioCaptureApp/AudioCaptureApp.csproj)）。テスト用に `public` へ広げない。
- **オーディオデバイス・Whisper モデルの実体を要求するテストは書かない。** それらはゲート（G3）を
  実行環境に依存させてしまう。境界の手前（純粋関数）までをテスト対象にする。
