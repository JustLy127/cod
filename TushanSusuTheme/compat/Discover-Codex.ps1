$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$result=@()
$processes=@(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe' OR Name='Codex.exe'" -ErrorAction SilentlyContinue)
foreach ($candidate in $processes) {
    if ($candidate.ExecutablePath -notmatch '^C:\\Program Files\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\(?:ChatGPT|Codex)\.exe$') { continue }
    if ($candidate.CommandLine -match '(?:^|\s)--type(?:=|\s)') { continue }
    $match=[regex]::Match($candidate.CommandLine,'--remote-debugging-port(?:=|\s+)(\d+)')
    if (!$match.Success) { continue }
    $port=[int]$match.Groups[1].Value
    if ($port -lt 1024 -or $port -gt 65535) { continue }
    $packageRoot=Split-Path -Parent (Split-Path -Parent $candidate.ExecutablePath)
    $manifestPath=Join-Path $packageRoot 'AppxManifest.xml'
    if (!(Test-Path -LiteralPath $manifestPath)) { continue }
    [xml]$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
    if ($manifest.Package.Identity.Name -ne 'OpenAI.Codex') { continue }
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    $owned=@($listeners | Where-Object { $_.OwningProcess -eq $candidate.ProcessId -and $_.LocalAddress -eq '127.0.0.1' })
    if (!$owned -or @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') }).Count -gt 0) { continue }
    $result+=[pscustomobject]@{pid=$candidate.ProcessId;port=$port;path=$candidate.ExecutablePath;startedAt=$candidate.CreationDate.ToUniversalTime().ToString('o')}
}
ConvertTo-Json -InputObject @($result) -Depth 4 -Compress
