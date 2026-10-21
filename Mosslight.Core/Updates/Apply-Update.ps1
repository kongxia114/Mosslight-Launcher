# ============================================================
#  Mosslight 自动更新的"落地"脚本
#
#  由 UpdateManager 启动。C# 那边**已经**把更新包解到 $StagedDir 了，
#  这个脚本只负责三件事，每一件都带重试和错误捕获：
#
#    1. 等主程序退出
#    2. 把 staged 里的文件覆盖到安装目录（**10 次 × 500ms 重试**，专治文件被占用）
#    3. 用用户**真实的 exe 名**重启（他可能把 Mosslight.exe 改成了 Moss.exe）
#
#  失败时：写 Logs\update_error.log + 一份 $ErrorMarker（下次启动由 UI 提示），
#  然后照样把启动器拉起来 —— 更新失败不该变成"程序没了"。
#
#  ⚠️ 这个文件必须以 **UTF-8 with BOM** 落盘：Windows PowerShell 5.1 读无 BOM 的
#     .ps1 会按 ANSI 解码，中文注释和字符串里的引号会被吃掉 → 直接语法错误。
#     UpdateManager 把它写到 Temp\Update\ 时也用的是带 BOM 的 UTF8。
# ============================================================
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$BaseDir,
    [Parameter(Mandatory = $true)][string]$StagedDir,
    [Parameter(Mandatory = $true)][string]$TargetExe,
    [string]$ErrorMarker = '',
    [int]$WaitSeconds = 60,
    [int]$RetryCount = 10,
    [int]$RetryDelayMs = 500
)

$ErrorActionPreference = 'Stop'

$logPath       = Join-Path $BaseDir 'Logs\update.log'
$errLog        = Join-Path $BaseDir 'Logs\update_error.log'
$targetExePath = Join-Path $BaseDir $TargetExe

function Write-UpdateLog([string]$text) {
    try {
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $logPath -Encoding UTF8 `
            -Value ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text)
    } catch { }
}

function Write-UpdateError([string]$text) {
    try {
        $dir = Split-Path -Parent $errLog
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Add-Content -LiteralPath $errLog -Encoding UTF8 `
            -Value ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text)
    } catch { }

    # 给 UI 看的标记：下次启动读它，弹「自动更新失败，请尝试手动下载替换」
    if ($ErrorMarker) {
        try {
            $dir = Split-Path -Parent $ErrorMarker
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            $json = @{ time = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'); error = $text } | ConvertTo-Json -Compress
            [System.IO.File]::WriteAllText($ErrorMarker, $json, (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }
}

function Start-Launcher {
    if (-not (Test-Path -LiteralPath $targetExePath)) {
        Write-UpdateLog "找不到 $targetExePath —— 需要用户手动处理"
        return
    }
    try {
        Start-Process -FilePath $targetExePath -WorkingDirectory $BaseDir -ErrorAction Stop | Out-Null
        Write-UpdateLog "已重新启动：$targetExePath"
    } catch {
        Write-UpdateError ('重新启动失败：' + $_.Exception.Message)
    }
}

# 覆盖单个文件：IOException / 权限问题都重试，重试用完才抛
function Copy-WithRetry([string]$From, [string]$To) {
    for ($i = 1; $i -le $RetryCount; $i++) {
        try {
            Copy-Item -LiteralPath $From -Destination $To -Force -ErrorAction Stop
            if ($i -gt 1) { Write-UpdateLog ("覆盖 {0} 第 {1} 次成功" -f (Split-Path -Leaf $To), $i) }
            return
        } catch [System.IO.IOException], [System.UnauthorizedAccessException] {
            Write-UpdateLog ("覆盖 {0} 第 {1}/{2} 次失败：{3}" -f `
                (Split-Path -Leaf $To), $i, $RetryCount, $_.Exception.Message)
            Start-Sleep -Milliseconds $RetryDelayMs
        }
    }
    throw ("覆盖 {0} 失败（重试 {1} 次）" -f $To, $RetryCount)
}

function Remove-WithRetry([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    for ($i = 1; $i -le $RetryCount; $i++) {
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            return
        } catch [System.IO.IOException], [System.UnauthorizedAccessException] {
            Start-Sleep -Milliseconds $RetryDelayMs
        }
    }
    Write-UpdateLog "删不掉 $Path（不影响这次更新）"
}

Write-UpdateLog "更新脚本启动：pid=$ProcessId，目标=$TargetExe，staged=$StagedDir"

try {
    if (-not (Test-Path -LiteralPath $StagedDir)) {
        throw "staged 目录不存在：$StagedDir"
    }

    # 1) 等主程序退出（exe 还开着的话文件是锁着的）
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Write-UpdateLog "等待主程序退出（最多 $WaitSeconds 秒）…"
        if (-not $process.WaitForExit($WaitSeconds * 1000)) {
            throw "等主程序退出超时（$WaitSeconds 秒）"
        }
    }
    Start-Sleep -Milliseconds 300

    # 2) 覆盖 exe：staged 里的 Mosslight.exe → 用户实际在用的名字
    $stagedExe = Join-Path $StagedDir 'Mosslight.exe'
    if (-not (Test-Path -LiteralPath $stagedExe)) {
        $found = Get-ChildItem -LiteralPath $StagedDir -Filter *.exe -File | Select-Object -First 1
        if ($found) { $stagedExe = $found.FullName }
    }
    if (-not (Test-Path -LiteralPath $stagedExe)) { throw "staged 里没有 exe" }

    Copy-WithRetry $stagedExe $targetExePath
    Write-UpdateLog "exe 已覆盖：$stagedExe → $targetExePath"

    # 用户改了名的话，别在目录里再留一个 Mosslight.exe
    if ($TargetExe -ne 'Mosslight.exe') {
        Remove-WithRetry (Join-Path $BaseDir 'Mosslight.exe')
    }

    # 3) 其它文件（以后包里要带东西的话，按原名覆盖）
    Get-ChildItem -LiteralPath $StagedDir -File | Where-Object { $_.FullName -ne $stagedExe } | ForEach-Object {
        Copy-WithRetry $_.FullName (Join-Path $BaseDir $_.Name)
    }

    Write-UpdateLog '解压产物已全部覆盖'
} catch {
    $reason = $_.Exception.Message
    Write-UpdateLog ('更新失败：' + $reason)
    Write-UpdateError ('自动更新失败：' + $reason)
} finally {
    # 不管成没成，都要把启动器拉起来
    Start-Launcher

    # 删自己：PowerShell 还占着这个 .ps1，交给隐藏的 cmd 等两秒再删
    Start-Process -FilePath 'cmd.exe' -WindowStyle Hidden `
        -ArgumentList '/c', ('ping 127.0.0.1 -n 3 >nul & del /f /q "' + $PSCommandPath + '"') | Out-Null
}
