<#
.SYNOPSIS
    PreToolUse フック — 破壊的コマンドと git 操作のゲート。

.DESCRIPTION
    Bash / PowerShell ツールの実行直前にコマンド文字列を検査する。

      DENY : 復旧不能な破壊 (ルート/ホームの再帰削除)
      ASK  : 復旧可能だが破壊的な操作、および全ての git 書き込み操作
             (git は「都度確認」がこのプロジェクトの規範のため)

    規範: docs/harness/00-ways-of-working.md #不可逆な操作は都度確認

.NOTES
    フェイルオープン。例外時は exit 0 で素通しする。
    入出力は UTF-8 に固定する (既定のコンソール コードページだと日本語が壊れる)。
#>

$ErrorActionPreference = 'Stop'

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Approve { exit 0 }

function Respond([string]$Decision, [string]$Reason) {
    $payload = @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = $Decision
            permissionDecisionReason = $Reason
        }
    }
    $payload | ConvertTo-Json -Depth 5 -Compress
    exit 0
}

try {
    $reader = [System.IO.StreamReader]::new(
        [Console]::OpenStandardInput(), [System.Text.UTF8Encoding]::new($false))
    $raw = $reader.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { Approve }

    # 注意: $input は PowerShell の予約自動変数のため使わないこと
    $hookInput = $raw | ConvertFrom-Json
    $cmd = $hookInput.tool_input.command
    if ([string]::IsNullOrWhiteSpace($cmd)) { Approve }

    # --- DENY: 復旧不能 ----------------------------------------------------
    $catastrophic = @(
        '\brm\s+(-[a-zA-Z]*\s+)*-?[a-zA-Z]*[rR][a-zA-Z]*[fF][a-zA-Z]*\s+/\s*$'
        '\brm\s+(-[a-zA-Z]*\s+)*-?[a-zA-Z]*[rR][a-zA-Z]*[fF][a-zA-Z]*\s+~'
        '\bformat\s+[a-zA-Z]:'
    )
    foreach ($p in $catastrophic) {
        if ($cmd -match $p) {
            Respond 'deny' (
                "復旧不能な破壊的コマンドのため拒否しました。`n" +
                "コマンド: $cmd`n" +
                "本当に必要な場合は、対象を限定した形で実行してください。")
        }
    }

    # --- ASK: 破壊的だが復旧可能 -------------------------------------------
    $destructive = @{
        '\brm\s+-'                                  = 'ファイル削除 (rm)'
        '\brmdir\b'                                 = 'ディレクトリ削除 (rmdir)'
        '\bRemove-Item\b'                           = 'ファイル/ディレクトリ削除 (Remove-Item)'
        '\bdel\s'                                   = 'ファイル削除 (del)'
        '\bgit\s+push\s+.*(--force|-f)\b'           = 'force push (共有履歴の書き換え)'
        '\bgit\s+reset\s+--hard\b'                  = '作業ツリーの破棄 (git reset --hard)'
        '\bgit\s+clean\s+-[a-zA-Z]*f'               = '未追跡ファイルの削除 (git clean -f)'
        '\bgit\s+checkout\s+--\s'                   = 'ファイル変更の破棄 (git checkout --)'
        '\bgit\s+branch\s+-[a-zA-Z]*D'              = 'ブランチの強制削除'
    }
    foreach ($p in $destructive.Keys) {
        if ($cmd -match $p) {
            Respond 'ask' (
                "破壊的な操作です: $($destructive[$p])`n" +
                "コマンド: $cmd`n" +
                "実行してよいか確認してください (docs/harness/00-ways-of-working.md)。")
        }
    }

    # --- ASK: git の書き込み操作は都度確認 ---------------------------------
    $gitWrite = @{
        '\bgit\s+commit\b'    = 'git commit'
        '\bgit\s+push\b'      = 'git push'
        '\bgit\s+add\b'       = 'git add'
        '\bgit\s+merge\b'     = 'git merge'
        '\bgit\s+rebase\b'    = 'git rebase'
        '\bgit\s+switch\b'    = 'ブランチ切替 (git switch)'
        '\bgh\s+pr\s+create'  = 'PR 作成'
    }
    foreach ($p in $gitWrite.Keys) {
        if ($cmd -match $p) {
            # 統合 (S8) に当たる操作には、品質ゲートの確認を添える
            $gateNote = ''
            if ($cmd -match '\bgit\s+(commit|push)\b' -or $cmd -match '\bgh\s+pr\s+create') {
                $gateNote =
                    "`n品質ゲート G1/G2/G3 が全て緑になっていることを確認してから統合すること" +
                    " (緑でないうちはコミットしない)。PR の宛先は develop " +
                    "(docs/harness/10-workflow.md S8)。"
            }
            Respond 'ask' (
                "git の書き込み操作は都度の明示的な承認が必要です: $($gitWrite[$p])`n" +
                "コマンド: $cmd`n" +
                "1 つの操作の承認は、別の操作 (commit → push 等) の承認を意味しません " +
                "(docs/harness/00-ways-of-working.md)。" + $gateNote)
        }
    }

    Approve
}
catch {
    Approve
}
