$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$exe = Join-Path $output 'ConduitClientLauncher.exe'

& $compiler /nologo /warnaserror+ /optimize+ /target:winexe /platform:x64 "/out:$exe" `
    "/reference:$framework\System.Net.Http.dll" /reference:System.Web.Extensions.dll `
    (Join-Path $PSScriptRoot 'Launcher.cs')
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed ($LASTEXITCODE)." }
Write-Output $exe
