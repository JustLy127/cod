[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Quiet,
    [string[]]$ShortcutDirectories
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/')
$packagePrefix = $packageRoot + [IO.Path]::DirectorySeparatorChar
$utf8 = New-Object Text.UTF8Encoding($false)
$resultPath = Join-Path $packageRoot 'setup-result.json'
$phase = 'environment'

function Save-SetupResult($Value) {
    $Value | Add-Member -MemberType NoteProperty -Name at -Value ([DateTime]::UtcNow.ToString('o')) -Force
    [IO.File]::WriteAllText($resultPath, ($Value | ConvertTo-Json -Depth 8), $utf8)
}

function Get-PackageFile([string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) {
        throw '文件清单中存在无效路径。'
    }
    $segments = @($RelativePath -split '[\\/]')
    if (@($segments | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0) {
        throw '文件清单中存在越界或无效路径。'
    }
    $fullPath = [IO.Path]::GetFullPath((Join-Path $packageRoot $RelativePath))
    if (!$fullPath.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw '文件清单中的路径超出了迁移包目录。'
    }
    return $fullPath
}

try {
    if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess) {
        throw '请在 64 位 Windows 中，用 64 位 Windows PowerShell 运行安装。'
    }

    $phase = 'package-files'
    $manifestPath = Join-Path $packageRoot 'checksums.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw '缺少 checksums.json，请重新完整解压迁移包。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $files = @($manifest.files)
    if ($files.Count -eq 0) { throw '文件校验清单为空。' }
    $seen = @{}
    foreach ($entry in $files) {
        $relativePath = [string]$entry.path
        $file = Get-PackageFile $relativePath
        $key = $file.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { throw ('文件校验清单含有重复项：' + $relativePath) }
        $seen[$key] = $true
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw ('迁移包文件缺失：' + $relativePath) }
        if ([string]$entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [string]$entry.size -notmatch '^\d+$') {
            throw ('文件校验信息无效：' + $relativePath)
        }
        if ((Get-Item -LiteralPath $file).Length -ne [int64]$entry.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine [string]$entry.sha256) {
            throw ('文件校验失败，请重新解压迁移包：' + $relativePath)
        }
    }

    $phase = 'official-codex'
    $packages = @(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop)
    if ($packages.Count -ne 1 -or [string]$packages[0].PackageFamilyName -cne 'OpenAI.Codex_2p2nqsd0c76g0') {
        throw '请先安装官方 Codex Windows 桌面客户端，并正常登录一次。'
    }
    $installRoot = [IO.Path]::GetFullPath([string]$packages[0].InstallLocation).TrimEnd('\','/')
    if ($installRoot -notmatch '^C:\\Program Files\\WindowsApps\\OpenAI\.Codex_[^\\]+$') {
        throw '此迁移包需要安装在 C:\Program Files\WindowsApps 下的官方 Codex。当前安装位置不受支持。'
    }
    $codexEntry = Join-Path $installRoot 'app\ChatGPT.exe'
    if (!(Test-Path -LiteralPath $codexEntry -PathType Leaf)) { throw '未找到官方 Codex 客户端入口。' }

    $phase = 'node-runtime'
    $node = Join-Path $packageRoot 'tools\node\node.exe'
    if (!(Test-Path -LiteralPath $node -PathType Leaf)) { throw '缺少随包提供的 Node 运行工具。' }
    $nodeRaw = & $node -p 'JSON.stringify({webSocket:globalThis.WebSocket instanceof Function,arch:process.arch,platform:process.platform})'
    if ($LASTEXITCODE -ne 0) { throw '随包提供的 Node 运行工具无法运行。' }
    $nodeInfo = ($nodeRaw -join [Environment]::NewLine) | ConvertFrom-Json
    if ($nodeInfo.webSocket -ne $true -or $nodeInfo.arch -cne 'x64' -or $nodeInfo.platform -cne 'win32') {
        throw 'Node 运行工具与本迁移包不兼容。'
    }

    if ($CheckOnly) {
        [pscustomobject]@{ success=$true; checkOnly=$true; verifiedFiles=$files.Count; codexVersion=[string]$packages[0].Version; nodeArchitecture=$nodeInfo.arch } | ConvertTo-Json -Depth 4
        if (!$Quiet) { Write-Host '检查通过。尚未安装快捷方式、启动 Codex 或更改主题。' -ForegroundColor Green }
        exit 0
    }

    $phase = 'configure-runtime'
    $themeRoot = Join-Path $packageRoot 'TushanSusuTheme'
    $launcherRoot = Join-Path $packageRoot 'FoxSpiritCodexBoot'
    [IO.File]::WriteAllText((Join-Path $themeRoot 'compat\node-path.txt'), $node + [Environment]::NewLine, $utf8)

    $phase = 'enable-theme'
    $LASTEXITCODE = 0
    $themeOutput = & (Join-Path $themeRoot 'Manage-Theme.ps1') -Action Enable -Quiet
    if ($LASTEXITCODE -ne 0) { throw '主题配置失败。' }
    $themeResult = ($themeOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($themeResult.configured -ne $true) { throw '未收到主题配置成功的结果。' }

    $phase = 'install-shortcuts'
    $shortcutArgs = @{ Action='Install' }
    if ($ShortcutDirectories) { $shortcutArgs.ShortcutDirectories = $ShortcutDirectories }
    $LASTEXITCODE = 0
    $shortcutOutput = & (Join-Path $launcherRoot 'Manage-Shortcuts.ps1') @shortcutArgs
    if ($LASTEXITCODE -ne 0) { throw '桌面入口创建失败。' }
    $shortcutParsed = ($shortcutOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $shortcutResult = @()
    foreach ($record in $shortcutParsed) { $shortcutResult += $record }
    if ($shortcutResult.Count -eq 0 -or @($shortcutResult | Where-Object { $_.Status -notin @('Installed','AlreadyInstalled') }).Count -gt 0) {
        throw '部分同名快捷方式已被修改，未覆盖这些入口。请查看安装记录。'
    }

    $result = [pscustomobject]@{ success=$true; checkOnly=$false; verifiedFiles=$files.Count; codexVersion=[string]$packages[0].Version; themeConfigured=$true; shortcuts=$shortcutResult; codexStarted=$false; petInstalled=$false }
    Save-SetupResult $result
    $result | ConvertTo-Json -Depth 8
    if (!$Quiet) {
        Write-Host ''
        Write-Host '安装完成。请从 Codex 菜单或托盘完整退出，再双击桌面“Codex - 涂山苏苏”。' -ForegroundColor Green
        Write-Host '宠物请按迁移说明，在新电脑的宠物设置中选择或导入。'
        Write-Host '请保留此文件夹；移动后需要重新安装入口。'
    }
    exit 0
} catch {
    $failure = [pscustomobject]@{ success=$false; checkOnly=[bool]$CheckOnly; phase=$phase; message=$_.Exception.Message; codexStarted=$false }
    if (!$CheckOnly) {
        try { Save-SetupResult $failure } catch { }
    }
    Write-Host ('安装未完成：' + $failure.message) -ForegroundColor Red
    if (!$CheckOnly) { Write-Host '可查看本目录的 setup-result.json，然后修复提示的问题并重新运行安装。' }
    exit 1
}
