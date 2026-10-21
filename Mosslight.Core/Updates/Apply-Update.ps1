# ============================================================
#  Mosslight 自动更新的"落地"脚本
#
#  由 UpdateManager 启动，参数都是绝对路径（路径里可能有空格，
#  所以调用方负责传对，脚本里一律用 -LiteralPath / 引号包住）。
#
#  步骤：等主程序退出 → 解压覆盖 → 删更新包 → 重新启动 → 删自己
#
#  ⚠️ 任何一步失败都要把启动器拉起来（finally 里那个 Start-Launcher）：
#     更新失败了，用户至少还有原来那份能用。
# ============================================================
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$BaseDir,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$ExeName = 'Mosslight.exe',
    [int]$WaitSeconds = 30
)

$ErrorActionPreference = 'Stop'

$logPath = Join-Path $BaseDir 'Logs\update.log'
$exePath = Join-Path $BaseDir $ExeName

function Write-UpdateLog([string]$text) {
    try {
        $dir = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
        Add-Content -LiteralPath $logPath -Value ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $text) -Encoding UTF8
    } catch {
        # 日志写不了不能影响更新本身
    }
}

function Start-Launcher {
    if (Test-Path -LiteralPath $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $BaseDir | Out-Null
        Write-UpdateLog "已重新启动：$exePath"
    } else {
        Write-UpdateLog "找不到 $exePath —— 更新包可能不完整，需要用户手动处理"
    }
}

Write-UpdateLog "更新脚本启动：pid=$ProcessId，包=$ZipPath，目录=$BaseDir"

try {
    # 1) 等主程序退出：exe 还在跑的话文件是锁着的，解压会失败
    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Write-UpdateLog "等待主程序退出（最多 $WaitSeconds 秒）…"
        if (-not $process.WaitForExit($WaitSeconds * 1000)) {
            Write-UpdateLog "等主程序退出超时，这次更新放弃（包留在 $ZipPath）"
            exit 1
        }
    }
    Start-Sleep -Milliseconds 500

    # 2) 解压覆盖（发布包的 zip 里 exe 直接在根，没有外层目录）
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $BaseDir -Force
    Write-UpdateLog '解压完成'

    # 3) 删掉更新包
    Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
} catch {
    Write-UpdateLog ('更新失败：' + $_.Exception.Message)
} finally {
    # 4) 不管成没成，都把启动器拉起来
    Start-Launcher

    # 5) 删自己：PowerShell 还占着这个 .ps1，交给一个隐藏的 cmd 等两秒再删
    Start-Process -FilePath 'cmd.exe' -WindowStyle Hidden `
        -ArgumentList '/c', ('ping 127.0.0.1 -n 3 >nul & del /f /q "' + $PSCommandPath + '"') | Out-Null
}
