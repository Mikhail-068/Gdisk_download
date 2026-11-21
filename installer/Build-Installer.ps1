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
$licenses = (Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $app) 'licenses')).Path

$compiler = Get-Command $MakeNsis -CommandType Application
$compilerDir = Split-Path -Parent $compiler.Source
$nsisNotice = Join-Path $compilerDir 'COPYING'
if (!(Test-Path -LiteralPath $nsisNotice -PathType Leaf)) {
    $nsisNotice = Join-Path (Split-Path -Parent $compilerDir) 'COPYING'
}
$nsisNotice = (Resolve-Path -LiteralPath $nsisNotice).Path

New-Item -Path (Split-Path -Parent $output) -ItemType Directory -Force | Out-Null
$compilerArguments = @(
    "/DAPP_FILE=$app"
    "/DOUTPUT_FILE=$output"
    "/DLICENSE_DIR=$licenses"
    "/DNSIS_NOTICE=$nsisNotice"
    (Join-Path $PSScriptRoot 'DriveDownload.nsi')
)
& $compiler.Source @compilerArguments
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $output -PathType Leaf)) {
    throw 'NSIS did not build the installer.'
}
Get-Item -LiteralPath $output
