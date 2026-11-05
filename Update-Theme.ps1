[CmdletBinding()]
param([string]$InstallRoot, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$utf8 = New-Object Text.UTF8Encoding($false)
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\','/')
$relativeFiles = @('TushanSusuTheme/compat/loader.mjs','TushanSusuTheme/compat/modern-appearance.js')
$committed = @()
$transactionLock = $null
$backupRoot = $null
$stateRoot = $null
$phase = 'preflight'

function Assert-PlainPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ($absolute -notmatch '^[A-Za-z]:\\') { throw '更新只支持本机磁盘上的普通文件夹。' }
    $cursor = $absolute
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('更新路径不能经过链接或重定向文件夹：' + $cursor)
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if (!$parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $absolute
}
function Package-Path([string]$Root, [string]$Relative) {
    if ($Relative -notin @('TushanSusuTheme/compat/loader.mjs','TushanSusuTheme/compat/modern-appearance.js','checksums.json','FoxSpiritCodexBoot/FoxSpiritCodexBoot.exe','FoxSpiritCodexBoot/launcher.json','TushanSusuTheme/theme/theme.json','TushanSusuTheme/compat/node-path.txt','TushanSusuTheme/runtime-data','TushanSusuTheme/runtime-data/compat-loader.lock')) { throw '内部更新路径无效。' }
    $absolute = Assert-PlainPath (Join-Path $Root $Relative)
    if (!$absolute.StartsWith(($Root.TrimEnd('\','/')+'\'),[StringComparison]::OrdinalIgnoreCase)) { throw '更新路径超出安装文件夹。' }
    return $absolute
}
function File-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Read-Manifest([string]$Root) {
    $manifestPath = Package-Path $Root 'checksums.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw ('缺少 checksums.json。请使用 Release 完整安装包，源码包需先完成构建：' + $Root) }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($manifest.files).Count -eq 0) { throw '校验清单为空。' }
    foreach ($relative in $relativeFiles) {
        $entries = @($manifest.files | Where-Object { ([string]$_.path).Replace('\','/') -ceq $relative })
        if ($entries.Count -ne 1 -or [string]$entries[0].sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [string]$entries[0].size -notmatch '^\d+$') { throw ('主题文件的校验记录缺失、重复或无效：' + $relative) }
    }
    return $manifest
}
function Official-Processes {
    return @(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe' OR Name='Codex.exe'" | Where-Object {
        $_.ExecutablePath -match '^[A-Za-z]:\\Program Files\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$'
    })
}
function Find-Installation {
    $shell = New-Object -ComObject WScript.Shell
    $roots = @()
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('CommonPrograms'))) {
        if (!$folder) { continue }
        $linkPath = Join-Path $folder 'Codex - 涂山苏苏.lnk'
        if (!(Test-Path -LiteralPath $linkPath -PathType Leaf)) { continue }
        $link = $shell.CreateShortcut($linkPath)
        $target = [IO.Path]::GetFullPath($link.TargetPath)
        if ($link.Arguments -or [IO.Path]::GetFileName($target) -ine 'FoxSpiritCodexBoot.exe' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($target)) -ine 'FoxSpiritCodexBoot') { throw '已有苏苏快捷方式被修改，无法自动确定安装位置。请用 -InstallRoot 指定旧安装文件夹。' }
        $roots += [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($target))
    }
    $unique = @($roots | Sort-Object -Unique)
    if ($unique.Count -ne 1) { throw '未找到唯一的苏苏安装位置。请保留原快捷方式，或用 -InstallRoot 指定旧安装文件夹。' }
    return $unique[0]
}
function Find-VerifiedWatcher([string]$Root) {
    $lockPath = Package-Path $Root 'TushanSusuTheme/runtime-data/compat-loader.lock'
    if (!(Test-Path -LiteralPath $lockPath -PathType Leaf)) { return $null }
    $record = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$record.pid -notmatch '^[1-9][0-9]*$') { throw '主题后台记录无效，请稍后重试。' }
    $candidate = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [int]$record.pid)
    if (!$candidate) { return $null }
    $nodePathFile = Package-Path $Root 'TushanSusuTheme/compat/node-path.txt'
    $nodePath = Assert-PlainPath ((Get-Content -LiteralPath $nodePathFile -Raw -Encoding UTF8).Trim())
    $loaderPath = Package-Path $Root 'TushanSusuTheme/compat/loader.mjs'
    $commandPattern = '^\s*(?:"{0}"|{0})\s+(?:"{1}"|{1})\s+--watch\s*$' -f [regex]::Escape($nodePath),[regex]::Escape($loaderPath)
    $started = [DateTimeOffset]::Parse([string]$record.startedAt,[Globalization.CultureInfo]::InvariantCulture)
    if ($candidate.Name -ine 'node.exe' -or $candidate.ExecutablePath -ine $nodePath -or $candidate.CommandLine -notmatch $commandPattern -or [Math]::Abs(($candidate.CreationDate.ToUniversalTime()-$started.UtcDateTime).TotalSeconds) -gt 15) {
        throw '主题后台身份已变化，未停止任何程序。请稍后重试更新。'
    }
    return $candidate
}
function Write-Result($Value) {
    $Value | Add-Member -NotePropertyName at -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
    if (!$CheckOnly -and $stateRoot) {
        $resultPath = Assert-PlainPath (Join-Path $stateRoot 'theme-update-result.json')
        [IO.File]::WriteAllText($resultPath,($Value | ConvertTo-Json -Depth 8),$utf8)
    }
    $Value | ConvertTo-Json -Depth 8
}

try {
    if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitProcess) { throw '请在 64 位 Windows PowerShell 中运行更新。' }
    $null = Assert-PlainPath $sourceRoot
    $sourceManifest = Read-Manifest $sourceRoot
    $updates = @()
    foreach ($relative in $relativeFiles) {
        $source = Package-Path $sourceRoot $relative
        $entry = @($sourceManifest.files | Where-Object { ([string]$_.path).Replace('\','/') -ceq $relative })[0]
        if (!(Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -ne [int64]$entry.size -or (File-Hash $source) -ine $entry.sha256) { throw ('新版主题文件校验失败，请重新下载完整安装包：' + $relative) }
        $updates += [pscustomobject]@{relative=$relative;source=$source;size=[int64]$entry.size;sha256=[string]$entry.sha256}
    }
    if (!$InstallRoot) { $InstallRoot = Find-Installation }
    $InstallRoot = (Assert-PlainPath $InstallRoot).TrimEnd('\','/')
    if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\') -or !(Test-Path -LiteralPath $InstallRoot -PathType Container)) { throw '请选择完整的旧安装文件夹，不能选择磁盘根目录。' }
    if ($InstallRoot.Equals($sourceRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '请将新版解压到另一个临时文件夹，再运行其中的 Update-Theme.cmd。' }
    foreach ($relative in @($relativeFiles)+@('FoxSpiritCodexBoot/FoxSpiritCodexBoot.exe','FoxSpiritCodexBoot/launcher.json','TushanSusuTheme/theme/theme.json')) {
        if (!(Test-Path -LiteralPath (Package-Path $InstallRoot $relative) -PathType Leaf)) { throw ('目标不是完整的苏苏安装文件夹，缺少：' + $relative) }
    }
    $theme = Get-Content -LiteralPath (Package-Path $InstallRoot 'TushanSusuTheme/theme/theme.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $launcher = Get-Content -LiteralPath (Package-Path $InstallRoot 'FoxSpiritCodexBoot/launcher.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($theme.id -cne 'tushan-susu-blossom' -or $launcher.ProcessPathContains -cne 'OpenAI.Codex_') { throw '目标文件夹不是本项目的苏苏主题安装。' }
    $targetManifest = Read-Manifest $InstallRoot
    $running = @(Official-Processes)
    $watcher = Find-VerifiedWatcher $InstallRoot
    if ($CheckOnly) {
        Write-Result ([pscustomobject]@{success=$true;checkOnly=$true;installRoot=$InstallRoot;verifiedSourceFiles=$updates.Count;codexRunning=($running.Count -gt 0);themeWatcherPresent=($null -ne $watcher)})
        exit 0
    }
    if ($running.Count -gt 0) { throw '请先保存任务，从 Codex 菜单或托盘选择“退出”并完整退出，再运行更新。关闭窗口或最小化不等于退出。' }

    $phase = 'backup'
    $stateRoot = Package-Path $InstallRoot 'TushanSusuTheme/runtime-data'
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $transactionPath = Assert-PlainPath (Join-Path $stateRoot 'theme-update.lock')
    $transactionLock = [IO.File]::Open($transactionPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $backupRoot = Join-Path $stateRoot ('theme-update-backup-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $null = Assert-PlainPath $backupRoot
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    $records = @()
    foreach ($relative in @($relativeFiles)+@('checksums.json')) {
        $target = Package-Path $InstallRoot $relative
        $backup = Join-Path $backupRoot ([IO.Path]::GetFileName($target))
        Copy-Item -LiteralPath $target -Destination $backup
        $oldHash = File-Hash $target
        if ((File-Hash $backup) -cne $oldHash) { throw '更新备份校验失败。' }
        $records += [pscustomobject]@{relative=$relative;target=$target;backup=$backup;oldHash=$oldHash;newHash=$null;staged=(Join-Path $backupRoot ([IO.Path]::GetFileName($target)+'.new'))}
    }
    foreach ($update in $updates) {
        $record = @($records | Where-Object { $_.relative -ceq $update.relative })[0]
        Copy-Item -LiteralPath $update.source -Destination $record.staged
        if ((File-Hash $record.staged) -ine $update.sha256) { throw '暂存主题文件校验失败。' }
        $entry = @($targetManifest.files | Where-Object { ([string]$_.path).Replace('\','/') -ceq $update.relative })[0]
        $entry.size = $update.size
        $entry.sha256 = $update.sha256
    }
    $manifestRecord = @($records | Where-Object { $_.relative -ceq 'checksums.json' })[0]
    [IO.File]::WriteAllText($manifestRecord.staged,($targetManifest | ConvertTo-Json -Depth 32),$utf8)
    if (@(Official-Processes).Count -gt 0) { throw 'Codex 在更新准备期间重新启动了，请完整退出后再试。' }
    $phase = 'stop-theme-watcher'
    $watcherLockPath = Package-Path $InstallRoot 'TushanSusuTheme/runtime-data/compat-loader.lock'
    $watcherLockHash = if (Test-Path -LiteralPath $watcherLockPath -PathType Leaf) { File-Hash $watcherLockPath } else { $null }
    $watcher = Find-VerifiedWatcher $InstallRoot
    if ($watcher) {
        $process = Get-Process -Id $watcher.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            if ([Math]::Abs(($process.StartTime.ToUniversalTime()-$watcher.CreationDate.ToUniversalTime()).TotalSeconds) -gt 1 -or $process.Path -ine $watcher.ExecutablePath) { throw '主题后台进程身份已变化，未停止程序。' }
            $process.Kill()
            if (!$process.WaitForExit(5000)) { throw '主题后台尚未结束，请稍后重试。' }
        }
    }
    # The killed Node process cannot run its finally block. Remove only the
    # unchanged lock we just verified, so a recycled PID cannot block next boot.
    if ($watcherLockHash -and (Test-Path -LiteralPath $watcherLockPath -PathType Leaf)) {
        $null = Assert-PlainPath $watcherLockPath
        if ((File-Hash $watcherLockPath) -cne $watcherLockHash) { throw '主题后台锁在更新期间变化，已保留新锁并停止更新。' }
        Remove-Item -LiteralPath $watcherLockPath
    }
    $phase = 'replace-theme'
    foreach ($record in $records) {
        $null = Assert-PlainPath $record.target
        if ((File-Hash $record.target) -cne $record.oldHash) { throw '更新期间文件被其他程序修改，已停止更新。' }
        $record.newHash = File-Hash $record.staged
        [IO.File]::Replace($record.staged,$record.target,[System.Management.Automation.Language.NullString]::Value)
        $committed += $record
    }
    foreach ($update in $updates) { if ((File-Hash (Package-Path $InstallRoot $update.relative)) -ine $update.sha256) { throw '更新后校验失败。' } }
    Write-Result ([pscustomobject]@{success=$true;checkOnly=$false;installRoot=$InstallRoot;updatedFiles=$relativeFiles;backupDirectory=$backupRoot;codexStarted=$false;configurationPreserved=$true})
    Write-Host '主题兼容补丁已更新。请双击原来的“Codex - 涂山苏苏”启动；临时下载的新文件夹可以删除。' -ForegroundColor Green
    Write-Host '原来的动画、设置、快捷方式和宠物素材保持不变。'
    exit 0
} catch {
    $message = $_.Exception.Message
    $rollbackErrors = @()
    for ($index=$committed.Count-1; $index -ge 0; $index--) {
        $record = $committed[$index]
        try {
            $null = Assert-PlainPath $record.target
            if ((File-Hash $record.target) -cne $record.newHash) { throw '文件已被其他程序修改，已保留当前文件和更新前备份。' }
            $rollbackStage = Join-Path $backupRoot ([IO.Path]::GetFileName($record.target)+'.rollback')
            Copy-Item -LiteralPath $record.backup -Destination $rollbackStage -Force
            [IO.File]::Replace($rollbackStage,$record.target,[System.Management.Automation.Language.NullString]::Value)
            if ((File-Hash $record.target) -cne $record.oldHash) { throw '恢复校验失败。' }
        } catch { $rollbackErrors += ($record.relative + ': ' + $_.Exception.Message) }
    }
    $failure = [pscustomobject]@{success=$false;checkOnly=[bool]$CheckOnly;phase=$phase;message=$message;rolledBack=($committed.Count -gt 0 -and $rollbackErrors.Count -eq 0);rollbackErrors=$rollbackErrors;backupDirectory=$backupRoot;codexStarted=$false}
    try { Write-Result $failure } catch { $failure | ConvertTo-Json -Depth 8 }
    Write-Host ('更新未完成：' + $message) -ForegroundColor Red
    if ($rollbackErrors.Count -gt 0) { Write-Host ('自动恢复未完成，请保留备份目录：' + $backupRoot) -ForegroundColor Red }
    exit 1
} finally {
    if ($transactionLock) { $transactionLock.Dispose() }
}
