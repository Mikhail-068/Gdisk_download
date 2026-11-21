[CmdletBinding()]
param(
    [string]$MakeNsis = 'makensis.exe',
    [string]$Application,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$Application) { $Application = Join-Path $project 'release\DriveDownload.exe' }
if (!$OutputPath) { $OutputPath = Join-Path $project 'release\DriveDownload-Setup-x64.exe' }

$app = (Resolve-Path -LiteralPath $Application).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ($app -eq $output) { throw 'The application and installer must use different paths.' }
$compiler = Get-Command $MakeNsis -CommandType Application
New-Item -Path (Split-Path -Parent $output) -ItemType Directory -Force | Out-Null

& $compiler.Source "/DAPP_FILE=$app" "/DOUTPUT_FILE=$output" (Join-Path $PSScriptRoot 'DriveDownload.nsi')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $output -PathType Leaf)) {
    throw 'NSIS did not build the installer.'
}
Get-Item -LiteralPath $output
