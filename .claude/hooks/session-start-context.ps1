<#
.SYNOPSIS
    SessionStart フック — セッション開始時にハーネスの法・現在ブランチ・進行中タスクを注入する。

.DESCRIPTION
    毎回 docs/harness/ を読みに行かなくても 4 つの法が効くように、
    要約と「現在のブランチ」「いま進行中のタスク」をセッション冒頭のコンテキストへ入れる。

.NOTES
    フェイルオープン。例外時は何も注入せず exit 0。
    出力は UTF-8 に固定する (既定のコンソール コードページだと日本語が壊れる)。
#>

$ErrorActionPreference = 'Stop'

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

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

    # --- 横断ルール: ブランチ運用 ------------------------------------------
    $branch = & git -C $repoRoot rev-parse --abbrev-ref HEAD 2>$null
    if (-not [string]::IsNullOrWhiteSpace($branch)) {
        $branch = $branch.Trim()
        $lines.Add('### ブランチ運用')
        $lines.Add("現在のブランチ: **$branch**")
        if (@('develop', 'main', 'master') -contains $branch) {
            $lines.Add('**保護ブランチ上にいる。** 新しいタスクに着手する前に develop を最新化し、作業ブランチを切ること。')
            $lines.Add('  1. `git switch develop` → `git pull --ff-only origin develop`')
            $lines.Add('  2. `git switch -c <feature|fix|maintenance>-<slug>` (1 タスク = 1 ブランチ。起票もこのブランチで)')
            $lines.Add('このブランチのままでは .cs / .xaml の編集を guard-source-edit.ps1 が deny で止める。')
        }
        else {
            $lines.Add('commit / push / PR 作成は品質ゲート G1/G2/G3 が全て緑になってから。PR の宛先は develop。')
        }
        $lines.Add('')
    }

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
