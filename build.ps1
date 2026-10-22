[CmdletBinding()]
param(
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[void](New-Item -ItemType Directory -Path $outputPath -Force)
$outputFile = Join-Path $outputPath 'ScreenDimmer.exe'
Push-Location $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$outputFile" /win32manifest:app.manifest /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll ScreenDimmer.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Write-Output $outputFile
}
finally { Pop-Location }
