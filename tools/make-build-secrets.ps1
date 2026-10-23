<#
  把编译期凭据写进 Mosslight.Core\Configuration\BuildSecrets.cs（**只给 CI / 本地调试用，别提交**）。
  仓库里那份永远是空占位。

  用法（本地）：
      $env:MOSSLIGHT_LITTLESKIN_CLIENT_ID = '1640'
      $env:MOSSLIGHT_LITTLESKIN_CLIENT_SECRET = '...'
      pwsh tools\make-build-secrets.ps1

  用法（CI）：
      & ./tools/make-build-secrets.ps1 -Entries @(
          "littleskin.client_id=$env:LS_ID",
          "littleskin.client_secret=$env:LS_SECRET")

  ⚠️ 没有值的条目直接跳过（GitHub 上没建的 secret 解析出来就是空串），
     不会写出 `"key" = ""` 这种占位，`BuildSecrets.Has(key)` 就能如实返回 false。
#>
param(
    [string[]]$Entries = @(),
    [string]$Path = 'Mosslight.Core/Configuration/BuildSecrets.cs'
)

$ErrorActionPreference = 'Stop'

# 本机调试：没显式传 -Entries 就把 MOSSLIGHT_* 环境变量收进来
if ($Entries.Count -eq 0) {
    Get-ChildItem Env: | Where-Object { $_.Name -like 'MOSSLIGHT_*' } | ForEach-Object {
        $key = $_.Name.Substring('MOSSLIGHT_'.Length).ToLowerInvariant().Replace('_', '.')
        $Entries += "$key=$($_.Value)"
    }
}

# 从命令行手工传时只能给一个字符串：允许用 ';' 分隔多条
if ($Entries.Count -eq 1 -and $Entries[0] -like '*;*') {
    $Entries = $Entries[0].Split(';')
}

$pairs = @()
foreach ($entry in $Entries) {
    if ([string]::IsNullOrWhiteSpace($entry)) { continue }
    $eq = $entry.IndexOf('=')
    if ($eq -le 0) { throw "条目格式应为 key=value：$entry" }
    $key = $entry.Substring(0, $eq).Trim().ToLowerInvariant()
    $value = $entry.Substring($eq + 1).Trim()
    if ([string]::IsNullOrEmpty($value)) { continue }     # 空值 = 没配，别写进去
    $pairs += [pscustomobject]@{ Key = $key; Value = $value }
}

function Escape([string]$text) {
    return $text.Replace('\', '\\').Replace('"', '\"')
}

$lines = $pairs | ForEach-Object {
    '        ["' + (Escape $_.Key) + '"] = "' + (Escape $_.Value) + '",'
}

$body = if ($lines.Count -gt 0) { $lines -join "`n" } else { '' }

$content = @"
using System.Collections.Generic;

namespace Mosslight.Core.Configuration;

// ⚠️ 这个文件是**生成**出来的（tools\make-build-secrets.ps1 / CI），不要提交它的真值版本。
public static class BuildSecrets
{
    private static readonly Dictionary<string, string> Injected =
        new(StringComparer.OrdinalIgnoreCase)
    {
$body
    };

    public static string Get(string key)
    {
        if (Injected.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        return Environment.GetEnvironmentVariable(EnvName(key))?.Trim() ?? string.Empty;
    }

    public static bool Has(string key) => Get(key).Length > 0;

    public static string EnvName(string key)
        => "MOSSLIGHT_" + key.Replace('.', '_').Replace('-', '_').ToUpperInvariant();
}

public static class SecretKeys
{
    public const string LittleSkinClientId = "littleskin.client_id";
    public const string LittleSkinClientSecret = "littleskin.client_secret";
    public const string MicrosoftClientId = "microsoft.client_id";
    public const string MicrosoftTenant = "microsoft.tenant";
}
"@

[System.IO.File]::WriteAllText(
    (Join-Path (Get-Location) $Path),
    $content,
    (New-Object System.Text.UTF8Encoding($false)))

$names = ($pairs | ForEach-Object { $_.Key }) -join ', '
Write-Host "已写入 $Path（$($pairs.Count) 条：$names）"
Write-Host '⚠️ 别提交这个文件：git status 里看到它就把改动丢掉。'
