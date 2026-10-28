<#
.SYNOPSIS
  Builds a self-contained, single-file AgentReclaim.exe (no .NET install needed), zips it and writes
  the zip's SHA-256 (the in-app updater refuses packages without a matching checksum).

.PARAMETER Version
  Overrides the version from the project file (e.g. to build a test package).

.PARAMETER CertThumbprint
  Optional code-signing certificate (in your CurrentUser\My store) to sign the exe with signtool.
  Unsigned builds work, but SmartScreen will warn on other machines.

.EXAMPLE
  ./build/publish.ps1
  ./build/publish.ps1 -CertThumbprint 0123ABCD...
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [string]$OutputRoot,
    [string]$CertThumbprint,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\AgentReclaim.App\AgentReclaim.App.csproj"
if (-not $OutputRoot) { $OutputRoot = Join-Path $root "dist" }
$out = Join-Path $OutputRoot $Runtime

if (-not $SkipTests) {
    dotnet test (Join-Path $root "tests\AgentReclaim.Core.Tests") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }
}

$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:IncludeSourceRevisionInInformationalVersion=false @versionArgs -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $out "AgentReclaim.exe"
if ($CertThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found; install the Windows SDK." }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
}

$productVersion = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$zipName = "AgentReclaim-$productVersion-$Runtime.zip"
$zip = Join-Path $OutputRoot $zipName
Compress-Archive -Path $exe, (Join-Path $root "README.md") -DestinationPath $zip -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $zipName" -Encoding ascii -NoNewline

"Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, version $productVersion)"
"Zipped $zip"
"SHA-256 $hash"
