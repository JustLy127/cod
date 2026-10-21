[CmdletBinding()]
param(
    [ValidateSet('Enable','Restore','Status')][string]$Action='Status',
    [switch]$Quiet,
    [string]$LauncherConfig,
    [string]$StateRoot
)
$ErrorActionPreference='Stop'
$packageRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$engine=Join-Path $packageRoot 'runtime\OpenChatGPTSkin.exe'
$dataRoot=Join-Path $packageRoot 'runtime-data'
$archive=Join-Path $packageRoot 'tushan-susu-blossom-1.0.0.ocskin'
$compatDisabled=Join-Path $dataRoot 'compat-disabled'
if (!$LauncherConfig) { $LauncherConfig=Join-Path $packageRoot '..\FoxSpiritCodexBoot\launcher.json' }
$LauncherConfig=[IO.Path]::GetFullPath($LauncherConfig)
if (!$StateRoot) { $StateRoot=Join-Path $packageRoot '.integration' }
$StateRoot=[IO.Path]::GetFullPath($StateRoot)
$beforePath=Join-Path $StateRoot 'launcher-before.json'
$expected=[ordered]@{
    ThemeRuntime='..\TushanSusuTheme\runtime\OpenChatGPTSkin.exe'
    ThemeDataRoot='..\TushanSusuTheme\runtime-data'
    ThemeId='tushan-susu-blossom'
    ThemeVersion='1.0.0'
}
function Invoke-Engine([string[]]$Arguments) {
    $oldPreference=$ErrorActionPreference
    try {
        $ErrorActionPreference='Continue'
        $output=@(& $engine @Arguments 2>&1)
        $code=$LASTEXITCODE
    } finally { $ErrorActionPreference=$oldPreference }
    $raw=($output | ForEach-Object { $_.ToString() }) -join "`n"
    if ($code -ne 0) { throw ('主题工具返回错误：'+$raw) }
    return $raw | ConvertFrom-Json
}
function Save-Config($Value) {
    $temporary=$LauncherConfig+'.theme-pending'
    $json=$Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($temporary,$json,(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $LauncherConfig -Force
}
try {
    if (!(Test-Path -LiteralPath $engine -PathType Leaf)) { throw '主题运行工具缺失，请保留完整 runtime 目录。' }
    if ($Action -eq 'Status') {
        $native=Invoke-Engine @('runtime','status','--data-root',$dataRoot)
        $compatPath=Join-Path $dataRoot 'compat-status.json'
        $compat=if (Test-Path -LiteralPath $compatPath) { Get-Content -LiteralPath $compatPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        [pscustomobject]@{native=$native;compatibility=$compat} | ConvertTo-Json -Depth 12
        return
    }
    $config=Get-Content -LiteralPath $LauncherConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($Action -eq 'Enable') {
        $null=Invoke-Engine @('theme','validate','--dir',(Join-Path $packageRoot 'theme'))
        $null=Invoke-Engine @('runtime','import','--theme-file',$archive,'--data-root',$dataRoot)
        if (Test-Path -LiteralPath $compatDisabled) { Remove-Item -LiteralPath $compatDisabled }
        New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
        if (!(Test-Path -LiteralPath $beforePath)) { Copy-Item -LiteralPath $LauncherConfig -Destination $beforePath }
        foreach ($entry in $expected.GetEnumerator()) { $config | Add-Member -MemberType NoteProperty -Name $entry.Key -Value $entry.Value -Force }
        Save-Config $config
        $result=[pscustomobject]@{configured=$true;theme=$expected.ThemeId;version=$expected.ThemeVersion;activation='next-full-exit-and-launch';runningSessionChanged=$false}
        $message='苏苏主题已接入启动图标。请从 Codex 菜单或托盘完整退出，再双击桌面“Codex - 涂山苏苏”。'
    } else {
        [IO.File]::WriteAllText($compatDisabled,'disabled',(New-Object Text.UTF8Encoding($false)))
        $nodePath=Join-Path $packageRoot 'compat\node-path.txt'
        if (Test-Path -LiteralPath $nodePath) {
            $node=(Get-Content -LiteralPath $nodePath -Raw -Encoding UTF8).Trim()
            if (Test-Path -LiteralPath $node) { & $node (Join-Path $packageRoot 'compat\loader.mjs') --restore | Out-Null; if ($LASTEXITCODE -ne 0) { throw '兼容主题恢复未完成，请查看 runtime-data\compat.log。' } }
        }
        $status=Invoke-Engine @('runtime','status','--data-root',$dataRoot)
        if ($status.controllerAvailable -eq $true) { $null=Invoke-Engine @('runtime','restore','--data-root',$dataRoot) }
        if ($config.ThemeId -eq $expected.ThemeId) {
            $before=if (Test-Path -LiteralPath $beforePath) { Get-Content -LiteralPath $beforePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
            foreach ($entry in $expected.GetEnumerator()) {
                $current=$config.PSObject.Properties[$entry.Key]
                if ($current -and $current.Value -eq $entry.Value) {
                    $old=if ($before) { $before.PSObject.Properties[$entry.Key] } else { $null }
                    if ($old) { $current.Value=$old.Value } else { $config.PSObject.Properties.Remove($entry.Key) }
                }
            }
            Save-Config $config
        }
        $result=[pscustomobject]@{configured=$false;theme=$expected.ThemeId;restoredLauncher=$true;controllerWasRunning=$status.controllerAvailable}
        $message='已恢复原有启动方式。如果当前运行的是主题会话，请从 Codex 菜单或托盘正常退出一次，完成恢复。'
    }
    $result | ConvertTo-Json -Depth 5
    if (!$Quiet) { Add-Type -AssemblyName PresentationFramework; $null=[System.Windows.MessageBox]::Show($message,'涂山苏苏 · Codex 主题') }
} catch {
    if (!$Quiet) { Add-Type -AssemblyName PresentationFramework; $null=[System.Windows.MessageBox]::Show($_.Exception.Message,'苏苏主题设置未完成') }
    Write-Error $_
    exit 1
}
