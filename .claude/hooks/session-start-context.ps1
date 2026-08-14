<#
.SYNOPSIS
    SessionStart フック — セッション開始時にハーネスの法と進行中タスクを注入する。

.DESCRIPTION
    毎回 docs/harness/ を読みに行かなくても 4 つの法が効くように、
    要約と「いま進行中のタスク」をセッション冒頭のコンテキストへ入れる。

.NOTES
    フェイルオープン。例外時は何も注入せず exit 0。
#>

$ErrorActionPreference = 'Stop'

function Emit([string]$Context) {
    $payload = @{
        hookSpecificOutput = @{
            hookEventName    = 'SessionStart'
            additionalContext = $Context
        }
    }
    $payload | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

try {
    $repoRoot = & git rev-parse --show-toplevel 2>$null
    if ([string]::IsNullOrWhiteSpace($repoRoot)) { exit 0 }
    $repoRoot = $repoRoot.Trim()

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('## 開発ハーネス (docs/harness/) — 常時適用')
    $lines.Add('')
    $lines.Add('作業は例外なく次の 4 つの法に従う。詳細は docs/harness/00-ways-of-working.md。')
    $lines.Add('')
    $lines.Add('1. **タスク先行** — タスクを起票していない作業はしない。進捗は docs/tasks/backlog.md でのみ管理する。')
    $lines.Add('2. **仕様書先行** — 仕様が変わるなら、ソースより先に docs/spec/ を直す。docs/spec/ が唯一の正。')
    $lines.Add('3. **アーキテクチャ優先** — Models/ViewModels/Services の 3 層と依存方向を都合で曲げない。変えるなら先に docs/adr/ に ADR を書く。')
    $lines.Add('4. **品質ゲート** — 下の 3 つを全て通すまで「完了」と言わない。無効化して通すのは禁止。')
    $lines.Add('   - `dotnet build AudioCaptureApp.slnx -c Debug` (警告 0 件)')
    $lines.Add('   - `dotnet format AudioCaptureApp.slnx --verify-no-changes` (差分なし)')
    $lines.Add('   - `dotnet test AudioCaptureApp.slnx -c Debug` (全件成功)')
    $lines.Add('')
    $lines.Add('git の commit / push / add は都度の明示的な依頼があるときだけ実行する。')
    $lines.Add('')

    # --- 進行中タスク ------------------------------------------------------
    $backlog = Join-Path $repoRoot 'docs/tasks/backlog.md'
    if (Test-Path -LiteralPath $backlog) {
        $inProgress = Select-String -LiteralPath $backlog -Pattern '^\s*-\s*\[~\]' |
            ForEach-Object { $_.Line.Trim() }

        $lines.Add('### いま進行中のタスク')
        if ($inProgress) {
            foreach ($t in $inProgress) { $lines.Add($t) }
        }
        else {
            $lines.Add('(なし) — ソースを編集する前に docs/tasks/backlog.md へ起票し、状態を [~] にすること。')
        }
        $lines.Add('')
    }

    Emit ($lines -join "`n")
}
catch {
    exit 0
}
