param([string]$OutputDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler is unavailable.' }
$references = @('System.dll','System.Core.dll','System.Management.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll',
    'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll')
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',
    ('/out:' + (Join-Path $OutputDirectory 'FoxSpiritCodexBoot.exe')))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $framework $reference) }
$compilerArgs += Join-Path $PSScriptRoot 'FoxSpiritCodexBoot.cs'
$compilerArgs += Join-Path $PSScriptRoot 'OfficialWarmActivation.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed: $LASTEXITCODE" }
Write-Output (Join-Path $OutputDirectory 'FoxSpiritCodexBoot.exe')
