[CmdletBinding()]
param([string]$RuntimeExe,[string]$DataRoot,[string]$ThemeId='tushan-susu-blossom',[string]$ThemeVersion='1.0.0')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)
if (!$DataRoot) { $DataRoot=Join-Path $root 'runtime-data' }
$DataRoot=[IO.Path]::GetFullPath($DataRoot)
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
$resultPath=Join-Path $DataRoot 'launch-result.json'
function Save-Result($value) {
    $value | Add-Member -NotePropertyName at -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
try {
    if ($ThemeId -ne 'tushan-susu-blossom' -or $ThemeVersion -ne '1.0.0') { throw '启动器与主题版本不匹配。' }
    if (Test-Path -LiteralPath (Join-Path $DataRoot 'compat-disabled')) { throw '苏苏主题目前处于关闭状态，请先启用主题。' }
    $node=(Get-Content -LiteralPath (Join-Path $root 'compat\node-path.txt') -Raw -Encoding UTF8).Trim()
    if (!(Test-Path -LiteralPath $node -PathType Leaf)) { throw '兼容加载器所需的本机 Node 运行工具缺失。' }
    $loader=Join-Path $root 'compat\loader.mjs'
    $managed=Join-Path $root 'compat\Start-ManagedCodex.ps1'
    $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
    $out=Join-Path $DataRoot ('launch-output-'+$stamp+'.json')
    $err=Join-Path $DataRoot ('launch-error-'+$stamp+'.log')
    $startedAt=[DateTime]::UtcNow
    $launch=Start-Process -FilePath $powershell -WorkingDirectory $root -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$managed+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    $launchCode=$launch.ExitCode
    if ($null -eq $launchCode) { throw '未能读取客户端启动结果。' }
    $launchDetail=Get-Content -LiteralPath $out -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($launchCode -ne 0) {
        Save-Result ([pscustomobject]@{phase=$launchDetail.phase;success=$false;code=$launchDetail.code;userMessage=$launchDetail.message;outputFile=[IO.Path]::GetFileName($out);errorFile=[IO.Path]::GetFileName($err)})
        exit $launchCode
    }
    $watch=Start-Process -FilePath $node -WorkingDirectory $root -ArgumentList @(('"'+$loader+'"'),'--watch') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DataRoot ('compat-output-'+$stamp+'.log')) -RedirectStandardError (Join-Path $DataRoot ('compat-error-'+$stamp+'.log'))
    $deadline=[DateTime]::UtcNow.AddSeconds(35)
    do {
        $compatPath=Join-Path $DataRoot 'compat-status.json'
        if (Test-Path -LiteralPath $compatPath) {
            try {
                $state=Get-Content -LiteralPath $compatPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $fresh=([DateTime]::Parse($state.at).ToUniversalTime()) -ge $startedAt
                if ($fresh -and $state.state -eq 'applied' -and $state.verifiedPages -gt 0) {
                    Save-Result ([pscustomobject]@{phase='theme-applied';success=$true;launchPhase=$launchDetail.phase;compatibilityApplied=$true;verifiedPages=$state.verifiedPages;outputFile=[IO.Path]::GetFileName($out)})
                    exit 0
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    Save-Result ([pscustomobject]@{phase='theme-not-confirmed';success=$false;code='THEME_NOT_CONFIRMED';compatibilityLoaderStarted=$true;outputFile=[IO.Path]::GetFileName($out)})
    exit 1
} catch {
    Save-Result ([pscustomobject]@{phase='error';success=$false;code='THEME_LAUNCH_FAILED';message=$_.Exception.Message})
    exit 1
}
