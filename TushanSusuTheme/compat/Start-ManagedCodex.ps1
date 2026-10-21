[CmdletBinding()]
param(
    [switch]$InspectOnly,
    [ValidateRange(5,90)][int]$TimeoutSeconds=45
)

$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)

# Independent of the upstream runtime state machine. This script does not stop
# processes, modify installed files, attach to an unverified endpoint, or retry
# activation while an existing official root is present.

function Throw-ManagedError([string]$Code,[string]$Message) {
    $failure=New-Object InvalidOperationException($Message)
    $failure.Data['Code']=$Code
    throw $failure
}

function Read-PackageXml([string]$Path) {
    $settings=New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create($Path,$settings)
    try {
        $document=New-Object Xml.XmlDocument
        $document.XmlResolver=$null
        $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose() }
}

function Get-PackageSignature([string]$Path) {
    $signature=Get-AuthenticodeSignature -LiteralPath $Path -ErrorAction Stop
    $signer=''
    if ($null -ne $signature.SignerCertificate) {
        $signer=$signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)
    }
    [pscustomobject]@{status=[string]$signature.Status;signer=$signer}
}

function Test-EntryBlockMap([string]$PackageRoot,[string]$RelativePath) {
    $blockMap=Read-PackageXml (Join-Path $PackageRoot 'AppxBlockMap.xml')
    $files=@($blockMap.BlockMap.File | Where-Object { ([string]$_.Name).Replace('/','\') -ieq $RelativePath.Replace('/','\') })
    if ($files.Count -ne 1) { return $false }
    $stream=[IO.File]::OpenRead((Join-Path $PackageRoot $RelativePath))
    $sha=[Security.Cryptography.SHA256]::Create()
    try {
        if ([int64]$files[0].Size -ne $stream.Length) { return $false }
        foreach ($block in @($files[0].Block)) {
            $count=[int][Math]::Min(65536,$stream.Length-$stream.Position)
            if ($count -le 0) { return $false }
            $buffer=New-Object byte[] $count
            $read=0
            while ($read -lt $count) {
                $next=$stream.Read($buffer,$read,$count-$read)
                if ($next -le 0) { return $false }
                $read+=$next
            }
            if ([Convert]::ToBase64String($sha.ComputeHash($buffer)) -cne [string]$block.Hash) { return $false }
        }
        return $stream.Position -eq $stream.Length
    } finally { $sha.Dispose();$stream.Dispose() }
}

function Get-VerifiedCodexInstall {
    $packages=@(Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop)
    if ($packages.Count -ne 1) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' '未找到唯一的官方 Codex Appx 安装。'
    }
    $package=$packages[0]
    if ([string]$package.PackageFamilyName -cne 'OpenAI.Codex_2p2nqsd0c76g0' -or [string]::IsNullOrWhiteSpace([string]$package.InstallLocation)) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' 'Codex 软件包家族标识不符合官方安装。'
    }
    $packageRoot=[IO.Path]::GetFullPath([string]$package.InstallLocation).TrimEnd('\','/')
    $manifest=Read-PackageXml (Join-Path $packageRoot 'AppxManifest.xml')
    $applications=@($manifest.Package.Applications.Application | Where-Object { [string]$_.Id -ceq 'App' })
    if ($applications.Count -ne 1) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' '官方 Codex Appx 清单中的 App 入口不唯一。'
    }
    $application=$applications[0]
    $relativePath=([string]$application.Executable).Replace('/','\')
    if ([string]$manifest.Package.Identity.Name -cne 'OpenAI.Codex' -or
        [string]$manifest.Package.Identity.Publisher -cne 'CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B' -or
        [string]$manifest.Package.Identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        $relativePath -ine 'app\ChatGPT.exe' -or
        [string]$application.EntryPoint -cne 'Windows.FullTrustApplication') {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' 'Codex 清单身份、发行者或官方可执行入口核验失败。'
    }
    $entryPath=[IO.Path]::GetFullPath((Join-Path $packageRoot $relativePath))
    if ($entryPath -ine ($packageRoot+'\app\ChatGPT.exe') -or !(Test-Path -LiteralPath $entryPath -PathType Leaf)) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' '官方 Codex 可执行文件路径核验失败。'
    }
    $packageSignature=Get-PackageSignature (Join-Path $packageRoot 'AppxSignature.p7x')
    $catalogSignature=Get-PackageSignature (Join-Path $packageRoot 'AppxMetadata\CodeIntegrity.cat')
    $resourceSignature=Get-PackageSignature (Join-Path $packageRoot 'app\resources\codex.exe')
    $expectedSigner='50BDFD77-8903-4850-9FFE-6E8522F64D5B'
    if ($packageSignature.status -cne 'Valid' -or $packageSignature.signer -cne $expectedSigner -or
        $catalogSignature.status -cne 'Valid' -or $catalogSignature.signer -cne $expectedSigner -or
        $resourceSignature.status -cne 'Valid' -or $resourceSignature.signer -cne 'OpenAI OpCo, LLC') {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' 'Codex 软件包、目录或 OpenAI 资源签名核验失败。'
    }
    $entryBlockMapValid=Test-EntryBlockMap $packageRoot $relativePath
    if (!$entryBlockMapValid) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' 'Codex 主程序与官方软件包块映射不一致。'
    }
    [pscustomobject]@{
        packageRoot=$packageRoot;entryPath=$entryPath;identityName='OpenAI.Codex'
        packageFamilyName=[string]$package.PackageFamilyName;packageVersion=[string]$manifest.Package.Identity.Version
        packagePublisher=[string]$manifest.Package.Identity.Publisher;appId='App'
        appUserModelId='OpenAI.Codex_2p2nqsd0c76g0!App';entryRelativePath=$relativePath
        entryPoint=[string]$application.EntryPoint;packageSignatureStatus=$packageSignature.status
        packageSignerCommonName=$packageSignature.signer;catalogSignatureStatus=$catalogSignature.status
        catalogSignerCommonName=$catalogSignature.signer;resourceSignatureStatus=$resourceSignature.status
        resourceSignerCommonName=$resourceSignature.signer;entryBlockMapValid=$true
    }
}

function Get-OfficialCodexRoots($Install) {
    $processes=@(Get-CimInstance Win32_Process -Filter "Name='ChatGPT.exe' OR Name='Codex.exe'" -ErrorAction Stop)
    $byId=@{}
    foreach ($candidate in $processes) { $byId[[int]$candidate.ProcessId]=$candidate }
    # A denied process path cannot be used as evidence that Codex is absent.
    # Renderer paths may be hidden while their accessible official ancestor is known.
    foreach ($candidate in $processes) {
        if (![string]::IsNullOrWhiteSpace([string]$candidate.ExecutablePath)) { continue }
        $ancestor=$candidate
        $seen=@{}
        $resolved=$false
        for ($depth=0;$depth -lt 32;$depth++) {
            if (![string]::IsNullOrWhiteSpace([string]$ancestor.ExecutablePath)) { $resolved=$true;break }
            $ancestorId=[int]$ancestor.ProcessId
            if ($seen.ContainsKey($ancestorId)) { break }
            $seen[$ancestorId]=$true
            $parentId=[int]$ancestor.ParentProcessId
            if (!$byId.ContainsKey($parentId)) { break }
            $ancestor=$byId[$parentId]
        }
        if (!$resolved) { Throw-ManagedError 'PROCESS_INSPECTION_DENIED' '无法核验一个 ChatGPT/Codex 进程的路径，因此未再次启动应用。' }
    }
    $official=@($processes | Where-Object { [string]$_.ExecutablePath -ieq [string]$Install.entryPath })
    $officialIds=@{}
    foreach ($candidate in $official) { $officialIds[[int]$candidate.ProcessId]=$true }
    $roots=@()
    foreach ($candidate in $official) {
        if ($officialIds.ContainsKey([int]$candidate.ParentProcessId) -or [string]$candidate.CommandLine -match '(?:^|\s)--type(?:=|\s)') { continue }
        if ($null -eq $candidate.CreationDate) { Throw-ManagedError 'PROCESS_INSPECTION_DENIED' '无法核验 Codex 根进程的启动时间。' }
        $roots+=[pscustomobject]@{
            pid=[int]$candidate.ProcessId;parentPid=[int]$candidate.ParentProcessId
            startedAt=([DateTime]$candidate.CreationDate).ToUniversalTime().ToString('o')
            executablePath=[string]$candidate.ExecutablePath
        }
    }
    return $roots
}

function Get-VerifiedCodexEndpoints($Install,[object[]]$Roots,[string]$DiscoveryScriptPath) {
    if (!(Test-Path -LiteralPath $DiscoveryScriptPath -PathType Leaf)) {
        Throw-ManagedError 'CDP_DISCOVERY_MISSING' 'Codex 端口核验脚本缺失。'
    }
    $raw=@(& $DiscoveryScriptPath)
    try { $endpoints=@(($raw -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop) }
    catch { Throw-ManagedError 'CDP_DISCOVERY_INVALID' 'Codex 端口核验没有返回有效 JSON。' }
    $verified=@()
    foreach ($endpoint in $endpoints) {
        if ($null -eq $endpoint) { continue }
        $matchingRoots=@($Roots | Where-Object {
            [int]$_.pid -eq [int]$endpoint.pid -and [string]$_.executablePath -ieq [string]$Install.entryPath -and
            [string]$endpoint.path -ieq [string]$Install.entryPath -and [string]$_.startedAt -ceq [string]$endpoint.startedAt
        })
        if ($matchingRoots.Count -ne 1) { continue }
        $port=0
        if (![int]::TryParse([string]$endpoint.port,[ref]$port) -or $port -lt 1024 -or $port -gt 65535) { continue }
        $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
        $owned=@($listeners | Where-Object { [int]$_.OwningProcess -eq [int]$endpoint.pid -and [string]$_.LocalAddress -ceq '127.0.0.1' })
        $foreign=@($listeners | Where-Object { [string]$_.LocalAddress -notin @('127.0.0.1','::1') -or [int]$_.OwningProcess -ne [int]$endpoint.pid })
        if ($owned.Count -lt 1 -or $foreign.Count -gt 0) { continue }
        $verified+=[pscustomobject]@{
            root=$matchingRoots[0]
            endpoint=[pscustomobject]@{host='127.0.0.1';port=$port;owningPid=[int]$endpoint.pid;verified=$true}
        }
    }
    return $verified
}

function Get-FreeLoopbackPort {
    $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    try {
        $listener.Start()
        return [int]([Net.IPEndPoint]$listener.LocalEndpoint).Port
    } finally { $listener.Stop() }
}

function Invoke-OfficialAppActivation($Install,[int]$Port) {
    if ($Install.packageFamilyName -cne 'OpenAI.Codex_2p2nqsd0c76g0' -or $Install.appId -cne 'App' -or
        $Install.appUserModelId -cne 'OpenAI.Codex_2p2nqsd0c76g0!App' -or $Port -lt 1024 -or $Port -gt 65535) {
        Throw-ManagedError 'CODEX_IDENTITY_INVALID' 'Codex Appx 激活身份无效。'
    }
    if ($null -eq ('TushanSusu.ManagedAppxActivator' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace TushanSusu {
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, out uint processId);
    }
    public static class ManagedAppxActivator {
        [DllImport("ole32.dll", ExactSpelling=true)]
        private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);
        public static uint Activate(string appUserModelId, string arguments) {
            Guid classId = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
            Guid interfaceId = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
            IApplicationActivationManager manager;
            // CLSCTX_LOCAL_SERVER keeps activation arguments alive independently
            // of this short-lived PowerShell launcher, as Microsoft recommends.
            int created = CoCreateInstance(ref classId, IntPtr.Zero, 4, ref interfaceId, out manager);
            if (created < 0) Marshal.ThrowExceptionForHR(created);
            if (manager == null) throw new InvalidOperationException("Windows Appx activation manager is unavailable.");
            try {
                uint processId;
                // AO_NOERRORUI: report activation errors as JSON instead of a Windows dialog.
                int result = manager.ActivateApplication(appUserModelId, arguments, 2, out processId);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                if (processId == 0) throw new InvalidOperationException("Windows Appx activation returned no process ID.");
                return processId;
            } finally { Marshal.FinalReleaseComObject(manager); }
        }
    }
}
'@ -ErrorAction Stop | Out-Null
    }
    $arguments='--remote-debugging-address=127.0.0.1 --remote-debugging-port='+$Port
    return [int][TushanSusu.ManagedAppxActivator]::Activate([string]$Install.appUserModelId,$arguments)
}

function New-ReadyResult([string]$Phase,[string]$Code,$Install,$Connection) {
    [pscustomobject]@{
        ok=$true;phase=$Phase;code=$Code;exitCode=0;install=$Install
        root=$Connection.root;endpoint=$Connection.endpoint
        pid=$Connection.root.pid;port=$Connection.endpoint.port;path=$Connection.root.executablePath;startedAt=$Connection.root.startedAt
    }
}

function Resolve-ExistingCodex($Install,[object[]]$Roots,[string]$DiscoveryScriptPath) {
    $connections=@(Get-VerifiedCodexEndpoints $Install $Roots $DiscoveryScriptPath)
    if ($connections.Count -gt 0) {
        $connection=$connections | Sort-Object { $_.root.startedAt } -Descending | Select-Object -First 1
        return New-ReadyResult 'reused' 'CODEX_CDP_REUSED' $Install $connection
    }
    [pscustomobject]@{
        ok=$false;phase='restart-required';code='CODEX_RESTART_REQUIRED';exitCode=2;install=$Install;roots=@($Roots)
        message='Codex 已在运行，但没有属于该官方进程的可核验本机调试端口。请先自行正常退出 Codex，再用主题入口启动；本脚本未关闭或再次激活应用。'
    }
}

function Invoke-ManagedCodex([switch]$OnlyInspect,[int]$WaitSeconds,[string]$DiscoveryScriptPath) {
    $install=Get-VerifiedCodexInstall
    if ($OnlyInspect) {
        return [pscustomobject]@{ok=$true;phase='inspected';code='CODEX_INSTALL_VERIFIED';exitCode=0;install=$install;appActivated=$false}
    }
    $roots=@(Get-OfficialCodexRoots $install)
    if ($roots.Count -gt 0) { return Resolve-ExistingCodex $install $roots $DiscoveryScriptPath }
    if (!(Test-Path -LiteralPath $DiscoveryScriptPath -PathType Leaf)) { Throw-ManagedError 'CDP_DISCOVERY_MISSING' 'Codex 端口核验脚本缺失，未启动应用。' }
    $port=Get-FreeLoopbackPort
    # Check again immediately before activation. Never send debugging arguments
    # to a pre-existing unmanaged application, including one started meanwhile.
    $roots=@(Get-OfficialCodexRoots $install)
    if ($roots.Count -gt 0) { return Resolve-ExistingCodex $install $roots $DiscoveryScriptPath }
    $activatedAfter=[DateTime]::UtcNow.AddSeconds(-1)
    $activatedPid=Invoke-OfficialAppActivation $install $port
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $lastDiscoveryCode='CDP_NOT_READY'
    do {
        $roots=@(Get-OfficialCodexRoots $install)
        $activatedRoots=@($roots | Where-Object { [int]$_.pid -eq $activatedPid -and [string]$_.executablePath -ieq [string]$install.entryPath })
        if ($activatedRoots.Count -eq 1) {
            $started=[DateTime]::Parse($activatedRoots[0].startedAt,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
            if ($started -lt $activatedAfter) { Throw-ManagedError 'CODEX_ACTIVATION_IDENTITY_MISMATCH' 'Windows 返回了更早的 Codex 进程；未连接或停止该进程。' }
            try {
                $connections=@(Get-VerifiedCodexEndpoints $install $activatedRoots $DiscoveryScriptPath | Where-Object { [int]$_.endpoint.port -eq $port })
                if ($connections.Count -eq 1) { return New-ReadyResult 'started' 'CODEX_MANAGED_STARTED' $install $connections[0] }
            } catch {
                $lastDiscoveryCode=if ($_.Exception.Data['Code']) { [string]$_.Exception.Data['Code'] } else { 'CDP_DISCOVERY_FAILED' }
            }
        }
        if ($clock.Elapsed.TotalSeconds -ge $WaitSeconds) { break }
        Start-Sleep -Milliseconds 350
    } while ($clock.Elapsed.TotalSeconds -lt $WaitSeconds)
    [pscustomobject]@{
        ok=$false;phase='error';code='CDP_NOT_READY';exitCode=1;install=$install
        activatedPid=$activatedPid;port=$port;lastDiscoveryCode=$lastDiscoveryCode;timeoutSeconds=$WaitSeconds
        message='已请求启动官方 Codex，但在限定时间内没有取得属于新进程的本机调试端口。应用保持运行，请查看启动日志。'
    }
}

$launchMutex=$null
$mutexHeld=$false
try {
    if (!$InspectOnly) {
        $launchMutex=New-Object Threading.Mutex($false,'Local\TushanSusuManagedCodexLaunch')
        try { $mutexHeld=$launchMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $mutexHeld=$true }
        if (!$mutexHeld) { Throw-ManagedError 'CODEX_LAUNCH_IN_PROGRESS' '另一个主题启动请求正在检查 Codex，请稍候。' }
    }
    $result=Invoke-ManagedCodex -OnlyInspect:$InspectOnly -WaitSeconds $TimeoutSeconds -DiscoveryScriptPath (Join-Path $PSScriptRoot 'Discover-Codex.ps1')
} catch {
    $errorCode=if ($_.Exception.Data['Code']) { [string]$_.Exception.Data['Code'] } else { 'CODEX_LAUNCH_FAILED' }
    $result=[pscustomobject]@{ok=$false;phase='error';code=$errorCode;exitCode=1;message=$_.Exception.Message}
} finally {
    if ($mutexHeld -and $null -ne $launchMutex) { $launchMutex.ReleaseMutex() }
    if ($null -ne $launchMutex) { $launchMutex.Dispose() }
}
$result | ConvertTo-Json -Depth 9 -Compress
exit [int]$result.exitCode
