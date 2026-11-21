$ErrorActionPreference = 'Stop'
$larkRoot = $PSScriptRoot
$larkFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$larkCompiler = Join-Path $larkFramework 'csc.exe'
$larkOutput = Join-Path $larkRoot 'bin'
New-Item -ItemType Directory -Force -Path $larkOutput | Out-Null
$larkSources = @(Get-ChildItem -LiteralPath (Join-Path $larkRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$larkReferences = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll', (Join-Path $larkFramework 'WPF\UIAutomationClient.dll'), (Join-Path $larkFramework 'WPF\UIAutomationTypes.dll'), (Join-Path $larkFramework 'WPF\WindowsBase.dll'))
$larkArgs = @('/codepage:65001','/nologo','/platform:x64','/optimize+','/warn:4',('/resource:' + (Join-Path $larkRoot 'THIRD-PARTY-NOTICES.txt') + ',LarkCodex.Notices')) + @($larkReferences | ForEach-Object { '/reference:' + $_ })
& $larkCompiler @larkArgs '/target:exe' ('/out:' + (Join-Path $larkOutput 'LarkCodex.Diagnostics.exe')) @larkSources
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed.' }
& $larkCompiler @larkArgs '/target:winexe' ('/out:' + (Join-Path $larkRoot 'LARK Codex.exe')) @larkSources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Write-Output 'Built LARK Codex.exe (standalone window) and bin\LarkCodex.Diagnostics.exe (development checks).'
