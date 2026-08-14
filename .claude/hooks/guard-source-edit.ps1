<#
.SYNOPSIS
    PreToolUse フック — 「タスク先行」「仕様書先行」の強制点。

.DESCRIPTION
    .cs / .xaml の編集直前に 2 つを確認する。

      1. タスク先行 (法 1): docs/tasks/backlog.md に進行中 [~] のタスクがあるか
      2. 仕様書先行 (法 2): docs/spec/ に未コミットの変更があるか
                            (AudioCaptureApp/ 配下の本体ソースのみ対象)

    どちらかを満たさない場合 "ask" を返し、ユーザーへ確認を求める。
    deny ではなく ask なのは、仕様に影響しない変更 (内部リファクタリング・
    テスト追加・静的解析警告の解消) が正当に存在するため。

    規範: docs/harness/00-ways-of-working.md
          docs/harness/50-spec-standards.md
          docs/harness/60-task-format.md

.NOTES
    フェイルオープン。フック自身の不具合が作業を止めないよう、
    例外時は必ず exit 0 で素通しする。
#>

$ErrorActionPreference = 'Stop'

function Approve {
    # 何も出力せず正常終了 = 通常のパーミッション判定に委ねる
    exit 0
}

function Ask([string]$Reason) {
    $payload = @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'ask'
            permissionDecisionReason = $Reason
        }
    }
    $payload | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { Approve }

    # 注意: $input は PowerShell の予約自動変数のため使わないこと
    $hookInput = $raw | ConvertFrom-Json
    $filePath = $hookInput.tool_input.file_path
    if ([string]::IsNullOrWhiteSpace($filePath)) { Approve }

    # 対象は C# / XAML のみ
    if ($filePath -notmatch '\.(cs|xaml)$') { Approve }

    # 自動生成物は対象外
    if ($filePath -match '[\\/](bin|obj)[\\/]') { Approve }

    $repoRoot = & git rev-parse --show-toplevel 2>$null
    if ([string]::IsNullOrWhiteSpace($repoRoot)) { Approve }
    $repoRoot = $repoRoot.Trim()

    $problems = [System.Collections.Generic.List[string]]::new()

    # --- 法 1: タスク先行 --------------------------------------------------
    $backlog = Join-Path $repoRoot 'docs/tasks/backlog.md'
    if (Test-Path -LiteralPath $backlog) {
        $inProgress = Select-String -LiteralPath $backlog -Pattern '^\s*-\s*\[~\]' -AllMatches
        if (-not $inProgress) {
            $problems.Add(
                "[タスク先行] docs/tasks/backlog.md に進行中 [~] のタスクがありません。" +
                "作業を始める前にタスクを起票し、状態を [~] にしてください " +
                "(docs/harness/60-task-format.md)。")
        }
    }

    # --- 法 2: 仕様書先行 (本体ソースのみ) ---------------------------------
    $normalized = $filePath -replace '\\', '/'
    if ($normalized -match '/AudioCaptureApp/') {
        $specChanges = & git -C $repoRoot status --porcelain -- 'docs/spec' 2>$null
        if ([string]::IsNullOrWhiteSpace($specChanges)) {
            $problems.Add(
                "[仕様書先行] docs/spec/ に未コミットの変更がありません。" +
                "仕様が変わる修正なら、ソースより先に docs/spec/ を更新してください " +
                "(docs/harness/50-spec-standards.md §2 の対応表)。" +
                "内部リファクタリング・テスト追加・静的解析警告の解消など" +
                "仕様に影響しない変更であれば、このまま承認して進めて構いません。")
        }
    }

    if ($problems.Count -gt 0) {
        $target = Split-Path -Leaf $filePath
        Ask ("$target の編集前に確認が必要です。`n`n" + ($problems -join "`n`n"))
    }

    Approve
}
catch {
    # フェイルオープン: フックの不具合で作業を止めない
    Approve
}
