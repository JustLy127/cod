[CmdletBinding()]
param(
    [string]$DependencyCache
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$repositoryPrefix = $repositoryRoot + [IO.Path]::DirectorySeparatorChar
$utf8 = New-Object Text.UTF8Encoding($false)
if ([string]::IsNullOrWhiteSpace($DependencyCache)) { $DependencyCache = Join-Path $repositoryRoot '.cache\downloads' }

if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess) {
    throw '请在 64 位 Windows 中，用 64 位 Windows PowerShell 运行构建。'
}

function Assert-SHA256([string]$Path, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw '依赖清单的 SHA256 无效。' }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw ('文件不存在：' + $Path) }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw ('SHA256 校验失败，文件未使用：' + $Path)
    }
}

function Assert-HTTPS([Uri]$Uri) {
    if (!$Uri.IsAbsoluteUri -or $Uri.Scheme -cne 'https' -or $Uri.UserInfo) {
        throw '依赖下载地址和重定向地址必须使用不含用户名密码的 HTTPS。'
    }
}

function Get-PinnedDependency($Dependency) {
    $name = [string]$Dependency.file
    $url = [string]$Dependency.url
    $expected = [string]$Dependency.sha256
    if ([string]::IsNullOrWhiteSpace([string]$Dependency.version) -or [string]::IsNullOrWhiteSpace($name) -or
        [IO.Path]::IsPathRooted($name) -or $name -match '[\\/:]' -or $name -in @('.','..') -or
        $expected -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'dependencies.json 的版本、文件名或 SHA256 无效。'
    }
    $downloadUri = [Uri]$url
    Assert-HTTPS $downloadUri
    $destination = Join-Path $cacheRoot $name
    if (Test-Path -LiteralPath $destination) {
        Assert-SHA256 $destination $expected
        Write-Host ('已校验缓存：' + $name)
        return $destination
    }

    $temporary = Join-Path $cacheRoot (([Guid]::NewGuid().ToString('N')) + '.download')
    try {
        Write-Host ('下载并校验：' + $name)
        # Handle redirects explicitly so every connection remains HTTPS.
        for ($redirect = 0; $redirect -le 8; $redirect++) {
            Assert-HTTPS $downloadUri
            $request = [Net.HttpWebRequest]::Create($downloadUri)
            $request.AllowAutoRedirect = $false
            $request.UserAgent = 'Codex-Susu-Windows-Installer'
            $request.Timeout = 60000
            $request.ReadWriteTimeout = 120000
            $response = $null
            $inputStream = $null
            $outputStream = $null
            try {
                $response = $request.GetResponse()
                $status = [int]$response.StatusCode
                if ($status -in @(301,302,303,307,308)) {
                    if ($redirect -eq 8 -or [string]::IsNullOrWhiteSpace($response.Headers['Location'])) {
                        throw '依赖下载重定向过多或缺少目标地址。'
                    }
                    $downloadUri = New-Object Uri($downloadUri, $response.Headers['Location'])
                    Assert-HTTPS $downloadUri
                    continue
                }
                if ($status -ne 200) { throw ('依赖下载返回 HTTP ' + $status) }
                $inputStream = $response.GetResponseStream()
                $outputStream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $inputStream.CopyTo($outputStream)
                break
            } finally {
                if ($outputStream) { $outputStream.Dispose() }
                if ($inputStream) { $inputStream.Dispose() }
                if ($response) { $response.Dispose() }
            }
        }
        Assert-SHA256 $temporary $expected
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        return $destination
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Copy-VerifiedFile([string]$Source, [string]$Destination, [string]$Expected) {
    Assert-SHA256 $Source $Expected
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if ([IO.Path]::GetFullPath($Source) -ieq [IO.Path]::GetFullPath($Destination)) { return }
    $temporary = Join-Path $parent (([Guid]::NewGuid().ToString('N')) + '.pending')
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        Assert-SHA256 $temporary $Expected
        Move-Item -LiteralPath $temporary -Destination $Destination -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Expand-SkinExecutable([string]$Archive, [string]$Destination, [string]$Expected) {
    if ($Expected -notmatch '^[a-fA-F0-9]{64}$') { throw '主题工具的 exeSHA256 无效。' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent (([Guid]::NewGuid().ToString('N')) + '.pending')
    $inputStream = $null
    $outputStream = $null
    try {
        $skinEntries = @($zip.Entries | Where-Object { ($_.FullName -replace '\\','/') -match '(^|/)OpenChatGPTSkin\.exe$' })
        if ($skinEntries.Count -ne 1) { throw '主题工具压缩包必须恰好包含一个 OpenChatGPTSkin.exe。' }
        # Never use archive entry paths as output paths: extract only the fixed executable.
        $inputStream = $skinEntries[0].Open()
        $outputStream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $inputStream.CopyTo($outputStream)
        $outputStream.Dispose()
        $outputStream = $null
        $inputStream.Dispose()
        $inputStream = $null
        Assert-SHA256 $temporary $Expected
        Move-Item -LiteralPath $temporary -Destination $Destination -Force
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        $zip.Dispose()
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Test-ManifestExclusion([string]$RelativePath) {
    if ($RelativePath -match '(^|/)(\.git|\.cache|dist|build|bin|obj|state|qa|logs|\.integration|runtime-data)(/|$)') { return $true }
    if ($RelativePath -match '\.(lnk|pending|download|tmp)$') { return $true }
    if ($RelativePath -in @('checksums.json','setup-result.json','FoxSpiritCodexBoot/launcher.json','TushanSusuTheme/compat/node-path.txt')) { return $true }
    return $false
}

$dependencyPath = Join-Path $repositoryRoot 'dependencies.json'
if (!(Test-Path -LiteralPath $dependencyPath -PathType Leaf)) { throw '缺少 dependencies.json。' }
$dependencies = Get-Content -LiteralPath $dependencyPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$dependencies.node -or !$dependencies.skin) { throw 'dependencies.json 必须包含 node 和 skin。' }
$cacheRoot = [IO.Path]::GetFullPath($DependencyCache)
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
# Windows PowerShell 5.1 supports TLS 1.2 on supported Windows versions.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$nodeArchive = Get-PinnedDependency $dependencies.node
$skinArchive = Get-PinnedDependency $dependencies.skin
Copy-VerifiedFile $nodeArchive (Join-Path $repositoryRoot 'tools\node\node.exe') ([string]$dependencies.node.sha256)
Expand-SkinExecutable $skinArchive (Join-Path $repositoryRoot 'TushanSusuTheme\runtime\OpenChatGPTSkin.exe') ([string]$dependencies.skin.exeSHA256)

$buildScript = Join-Path $repositoryRoot 'FoxSpiritCodexBoot\source\build.ps1'
if (!(Test-Path -LiteralPath $buildScript -PathType Leaf)) { throw '缺少开场启动器源码构建脚本。' }
$LASTEXITCODE = 0
& $buildScript -OutputDirectory (Join-Path $repositoryRoot 'FoxSpiritCodexBoot')
if ($LASTEXITCODE -ne 0) { throw ('开场启动器编译失败：' + $LASTEXITCODE) }
if (!(Test-Path -LiteralPath (Join-Path $repositoryRoot 'FoxSpiritCodexBoot\FoxSpiritCodexBoot.exe') -PathType Leaf)) {
    throw '编译后未找到开场启动器。'
}

$entries = @()
foreach ($file in @(Get-ChildItem -LiteralPath $repositoryRoot -Recurse -File -Force | Sort-Object FullName)) {
    $fullPath = [IO.Path]::GetFullPath($file.FullName)
    if (!$fullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw '文件清单包含仓库目录以外的路径。' }
    $relative = $fullPath.Substring($repositoryPrefix.Length).Replace('\','/')
    if (Test-ManifestExclusion $relative) { continue }
    $entries += [pscustomobject]@{ path=$relative; size=[int64]$file.Length; sha256=(Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
if ($entries.Count -eq 0) { throw '构建文件清单为空。' }
$manifest = [pscustomobject]@{ files=@($entries) }
$manifestPath = Join-Path $repositoryRoot 'checksums.json'
$manifestTemporary = Join-Path $repositoryRoot (([Guid]::NewGuid().ToString('N')) + '.pending')
try {
    [IO.File]::WriteAllText($manifestTemporary, ($manifest | ConvertTo-Json -Depth 5), $utf8)
    Move-Item -LiteralPath $manifestTemporary -Destination $manifestPath -Force
} finally {
    if (Test-Path -LiteralPath $manifestTemporary) { Remove-Item -LiteralPath $manifestTemporary -Force }
}
Write-Host ('构建完成，已生成校验清单：' + $entries.Count + ' 个文件。') -ForegroundColor Green
Write-Host '构建不会启动 Codex，也不会创建桌面入口；请运行 Install.cmd 安装。'
