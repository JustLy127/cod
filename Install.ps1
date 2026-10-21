[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Quiet,
    [string[]]$ShortcutDirectories,
    [string]$DependencyCache
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DependencyCache)) { $DependencyCache = Join-Path $PSScriptRoot '.cache\downloads' }
try {
    $required = @('FoxSpiritCodexBoot\FoxSpiritCodexBoot.exe','TushanSusuTheme\runtime\OpenChatGPTSkin.exe','tools\node\node.exe','checksums.json')
    $ready = $true
    foreach ($relative in $required) {
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $relative) -PathType Leaf)) { $ready = $false }
    }
    if (!$ready) {
        if (!$Quiet) { Write-Host '首次安装：下载并校验固定版本依赖，然后构建启动器。' }
        $LASTEXITCODE = 0
        & (Join-Path $PSScriptRoot 'scripts\Build.ps1') -DependencyCache $DependencyCache
        if ($LASTEXITCODE -ne 0) { throw ('构建未完成：' + $LASTEXITCODE) }
    }

    $setupArgs = @{ CheckOnly=[bool]$CheckOnly; Quiet=[bool]$Quiet }
    if ($ShortcutDirectories) { $setupArgs.ShortcutDirectories = $ShortcutDirectories }
    $LASTEXITCODE = 0
    & (Join-Path $PSScriptRoot 'Setup.ps1') @setupArgs
    exit $LASTEXITCODE
} catch {
    Write-Host ('安装未完成：' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
