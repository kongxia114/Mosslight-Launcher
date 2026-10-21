# 把 LittleSkin 的客户端凭据写进 LittleSkinSecrets.cs（**只给 CI / 本地调试用，别提交**）。
#
# 用法（本地）：
#   $env:MOSSLIGHT_LITTLESKIN_CLIENT_ID = '1640'
#   $env:MOSSLIGHT_LITTLESKIN_CLIENT_SECRET = '...'
#   pwsh tools\make-littleskin-secrets.ps1
#
# 用法（CI）：release.yml 里的「注入 LittleSkin 凭据」那一步就是用这个脚本，
# 只是参数从环境变量改成 -ClientId / -ClientSecret。
param(
    [string]$ClientId = $env:MOSSLIGHT_LITTLESKIN_CLIENT_ID,
    [string]$ClientSecret = $env:MOSSLIGHT_LITTLESKIN_CLIENT_SECRET,
    [string]$Path = 'Mosslight.Core/Services/Auth/LittleSkinSecrets.cs'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ClientId) -or [string]::IsNullOrWhiteSpace($ClientSecret)) {
    throw '缺少 ClientId / ClientSecret：请先设 MOSSLIGHT_LITTLESKIN_CLIENT_ID / _SECRET，或用 -ClientId / -ClientSecret 传进来'
}

# C# 字符串字面量转义：反斜杠和引号
function Escape([string]$text) {
    return $text.Replace('\', '\\').Replace('"', '\"')
}

$content = @"
using System.IO;

namespace Mosslight.Core.Services.Auth;

// ⚠️ 这个文件是**生成**出来的（tools\make-littleskin-secrets.ps1 / CI），不要提交它的真值版本。
public static class LittleSkinSecrets
{
    private const string BuildClientId = "$(Escape $ClientId)";
    private const string BuildClientSecret = "$(Escape $ClientSecret)";

    public const string RedirectUri = "http://127.0.0.1:25566/callback";

    public static string ClientId => BuildClientId;
    public static string ClientSecret => BuildClientSecret;
    public static bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    public static LittleSkinOptions CreateOptions(string? logDir = null) => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        RedirectUri = RedirectUri,
        LogDir = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs"),
    };
}
"@

[System.IO.File]::WriteAllText(
    (Join-Path (Get-Location) $Path),
    $content,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "已写入 $Path（ClientId=$ClientId，secret 长度 $($ClientSecret.Length)）"
Write-Host '⚠️ 别提交这个文件：git status 里看到它就把改动丢掉。'
