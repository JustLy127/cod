[CmdletBinding()]
param(
    [ValidateSet('Install','Restore','Inspect')][string]$Action = 'Install',
    [string[]]$ShortcutDirectories,
    [string]$StateDirectory
)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$launcher = Join-Path $appRoot 'FoxSpiritCodexBoot.exe'
$linkName = 'Codex - 涂山苏苏.lnk'
if (!$ShortcutDirectories) {
    $ShortcutDirectories = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
}
$allowedPaths = @($ShortcutDirectories | ForEach-Object { Join-Path ([IO.Path]::GetFullPath($_)) $linkName } | Select-Object -Unique)
if (!$StateDirectory) { $StateDirectory = Join-Path $appRoot '.integration' }
$stateRoot = [IO.Path]::GetFullPath($StateDirectory)
$backupRoot = Join-Path $stateRoot 'backups'
$statePath = Join-Path $stateRoot 'shortcuts.json'
$shell = New-Object -ComObject WScript.Shell
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Save-State($Value) {
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $tempPath = Join-Path $stateRoot 'shortcuts.pending.json'
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tempPath -Encoding UTF8
    Move-Item -LiteralPath $tempPath -Destination $statePath -Force
}
$state = [pscustomobject]@{ Version=1; Launcher=$launcher; Records=@() }
if (Test-Path -LiteralPath $statePath) { $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json }
$results = @()
if ($Action -eq 'Install') {
    if (!(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw '请完整保留启动器目录后再安装入口。' }
    $icon = $launcher + ',0'
    $official = @(Get-Process -Name ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*WindowsApps\OpenAI.Codex_*\app\ChatGPT.exe' } | Select-Object -First 1)
    if ($official.Count -gt 0) { $icon = $official[0].Path + ',0' }
    foreach ($path in $allowedPaths) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        $record = @($state.Records | Where-Object { $_.Path -eq $path } | Select-Object -First 1)
        if ($record.Count -gt 0 -and $record[0].Status -eq 'Pending') {
            $currentHash = if (Test-Path -LiteralPath $path) { Hash $path } else { $null }
            if ($record[0].InstalledHash -and $currentHash -eq $record[0].InstalledHash) {
                $record[0].Status = 'Installed'
                Save-State $state
            } elseif (($record[0].OriginalHash -and $currentHash -eq $record[0].OriginalHash) -or (!$record[0].OriginalHash -and !$currentHash)) {
                $record[0].Status = 'Restored'
                Save-State $state
            } else {
                $results += [pscustomobject]@{Path=$path;Status='SkippedPendingUserChange'}
                continue
            }
        }
        if ($record.Count -gt 0 -and $record[0].Status -eq 'Installed') {
            if ((Test-Path -LiteralPath $path) -and (Hash $path) -eq $record[0].InstalledHash) {
                $results += [pscustomobject]@{Path=$path;Status='AlreadyInstalled'}
            } else { $results += [pscustomobject]@{Path=$path;Status='SkippedUserChange'} }
            continue
        }
        $originalHash = $null
        $backupName = $null
        if (Test-Path -LiteralPath $path) {
            $originalHash = Hash $path
            New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
            $backupName = [Guid]::NewGuid().ToString('N') + '.lnk'
            $backupPath = Join-Path $backupRoot $backupName
            Copy-Item -LiteralPath $path -Destination $backupPath
            if ((Hash $backupPath) -ne $originalHash) { throw '入口备份校验失败。' }
        }
        $temporaryLink = Join-Path (Split-Path -Parent $path) ([Guid]::NewGuid().ToString('N') + '.lnk')
        $link = $shell.CreateShortcut($temporaryLink)
        $link.TargetPath = $launcher
        $link.WorkingDirectory = $appRoot
        $link.Arguments = ''
        $link.Description = '涂山苏苏主题开场，随后进入 Codex；Esc 跳过。'
        $link.IconLocation = $icon
        $link.Save()
        $check = $shell.CreateShortcut($temporaryLink)
        if ($check.TargetPath -ne $launcher -or $check.Arguments -ne '') { throw '入口写入后校验失败。' }
        $newRecord = [pscustomobject]@{Path=$path;OriginalHash=$originalHash;BackupName=$backupName;InstalledHash=(Hash $temporaryLink);Status='Pending'}
        $state.Records = @($state.Records | Where-Object { $_.Path -ne $path }) + @($newRecord)
        Save-State $state
        Copy-Item -LiteralPath $temporaryLink -Destination $path -Force
        if ((Hash $path) -ne $newRecord.InstalledHash) { throw '入口提交后校验失败，恢复记录已保留。' }
        Remove-Item -LiteralPath $temporaryLink
        $newRecord.Status = 'Installed'
        Save-State $state
        $results += [pscustomobject]@{Path=$path;Status='Installed';Target=$launcher}
    }
} elseif ($Action -eq 'Restore') {
    foreach ($record in $state.Records) {
        if ($record.Path -notin $allowedPaths) { throw '恢复记录包含非预期的入口位置。' }
        if ($record.Status -notin @('Installed','Pending')) { continue }
        if (!(Test-Path -LiteralPath $record.Path)) {
            $record.Status = 'UserRemoved'
            Save-State $state
            $results += [pscustomobject]@{Path=$record.Path;Status='UserRemoved'}
            continue
        }
        if ($record.Status -eq 'Pending' -and $record.OriginalHash -and (Hash $record.Path) -eq $record.OriginalHash) {
            $record.Status = 'Restored'
            Save-State $state
            $results += [pscustomobject]@{Path=$record.Path;Status='OriginalUnchanged'}
            continue
        }
        if ((Hash $record.Path) -ne $record.InstalledHash) {
            $results += [pscustomobject]@{Path=$record.Path;Status='SkippedUserChange'}
            continue
        }
        if ($record.OriginalHash) {
            if ($record.BackupName -notmatch '^[a-f0-9]{32}\.lnk$') { throw '入口备份文件名无效。' }
            $backupPath = Join-Path $backupRoot $record.BackupName
            if (!(Test-Path -LiteralPath $backupPath) -or (Hash $backupPath) -ne $record.OriginalHash) { throw '入口备份校验失败，已保留当前入口。' }
            Copy-Item -LiteralPath $backupPath -Destination $record.Path -Force
        } else { Remove-Item -LiteralPath $record.Path }
        $record.Status = 'Restored'
        Save-State $state
        $results += [pscustomobject]@{Path=$record.Path;Status='Restored'}
    }
    Save-State $state
} else {
    foreach ($path in $allowedPaths) {
        if (Test-Path -LiteralPath $path) {
            $link = $shell.CreateShortcut($path)
            $results += [pscustomobject]@{Path=$path;Target=$link.TargetPath;Arguments=$link.Arguments;WorkingDirectory=$link.WorkingDirectory}
        } else { $results += [pscustomobject]@{Path=$path;Status='Missing'} }
    }
}
$results | ConvertTo-Json -Depth 5
