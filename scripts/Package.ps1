[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
$repoPrefix = $repoRoot + '\'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'dist' }
foreach ($relative in @('FoxSpiritCodexBoot\FoxSpiritCodexBoot.exe','TushanSusuTheme\runtime\OpenChatGPTSkin.exe','tools\node\node.exe','checksums.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $repoRoot $relative) -PathType Leaf)) { throw 'Run scripts/Build.ps1 before packaging.' }
}
$metadata = Get-Content -LiteralPath (Join-Path $repoRoot 'package-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($metadata.packageVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
$cacheRoot = Join-Path $repoRoot '.cache'
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
$stageParent = Join-Path $cacheRoot ('package-' + [Guid]::NewGuid().ToString('N'))
$stageRoot = Join-Path $stageParent 'cod'
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
function Excluded([string]$Relative) {
    if ($Relative -match '(^|/)(\.git|\.cache|dist|build|bin|obj|state|qa|logs|\.integration|runtime-data)(/|$)') { return $true }
    if ($Relative -match '\.(lnk|pending|download|tmp)$') { return $true }
    return $Relative -in @('checksums.json','setup-result.json','TushanSusuTheme/compat/node-path.txt')
}
# Snapshot first; package staging is under the excluded cache directory.
$files = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($repoPrefix.Length).Replace('\','/')
    if (Excluded $relative) { continue }
    $target = Join-Path $stageRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
# A release always starts with a clean local-theme baseline, even when the
# maintainer has already installed this checkout on their own computer.
$configPath = Join-Path $stageRoot 'FoxSpiritCodexBoot\launcher.json'
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($name in @('ThemeRuntime','ThemeDataRoot','ThemeId','ThemeVersion')) { $config.PSObject.Properties.Remove($name) }
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($configPath,($config | ConvertTo-Json -Depth 8),$utf8)
$entries = @()
foreach ($file in Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Force | Sort-Object FullName) {
    $relative = $file.FullName.Substring($stageRoot.Length+1).Replace('\','/')
    if ($relative -eq 'FoxSpiritCodexBoot/launcher.json') { continue }
    $entries += [pscustomobject]@{path=$relative;size=[int64]$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[IO.File]::WriteAllText((Join-Path $stageRoot 'checksums.json'),(@{files=$entries} | ConvertTo-Json -Depth 5),$utf8)
$output = [IO.Path]::GetFullPath($OutputDirectory)
# Keep generated ZIPs under dist or cache so later builds never include them.
$distRoot = Join-Path $repoRoot 'dist'
if (!$output.Equals($distRoot,[StringComparison]::OrdinalIgnoreCase) -and
    !$output.StartsWith(($distRoot+'\'),[StringComparison]::OrdinalIgnoreCase) -and
    !$output.StartsWith(($cacheRoot+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must be under the repository dist or .cache directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archivePath = Join-Path $output ('cod-windows-x64-v' + $metadata.packageVersion + '.zip')
if (Test-Path -LiteralPath $archivePath) { throw 'The release ZIP already exists; choose a fresh output directory.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stageRoot,$archivePath,[IO.Compression.CompressionLevel]::Optimal,$true)
$sha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archivePath+'.sha256'),($sha256+'  '+[IO.Path]::GetFileName($archivePath)+[Environment]::NewLine),[Text.Encoding]::ASCII)
[pscustomobject]@{archive=$archivePath;bytes=(Get-Item -LiteralPath $archivePath).Length;sha256=$sha256;files=$entries.Count+2} | ConvertTo-Json
