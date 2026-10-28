<#
.SYNOPSIS
  Builds a self-contained, single-file AgentReclaim.exe (no .NET install needed), zips it and writes
  the zip's SHA-256 (the in-app updater refuses packages without a matching checksum).
  Also builds a per-user installer (AgentReclaim-<version>-win-x64-setup.exe) with its own SHA-256.

.PARAMETER Version
  Overrides the version from the project file (e.g. to build a test package).

.PARAMETER SkipInstaller
  Skips the per-user setup exe (built with Inno Setup 6 from build/installer.iss when ISCC is installed).

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
    [switch]$SkipTests,
    [switch]$SkipInstaller
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

function Sign-File([string]$path) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found; install the Windows SDK." }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing $path failed." }
}

function Write-Checksum([string]$path) {
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$path.sha256" -Value "$hash  $(Split-Path $path -Leaf)" -Encoding ascii -NoNewline
    $hash
}

$exe = Join-Path $out "AgentReclaim.exe"
if ($CertThumbprint) { Sign-File $exe }

$productVersion = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$zipName = "AgentReclaim-$productVersion-$Runtime.zip"
$zip = Join-Path $OutputRoot $zipName
Copy-Item (Join-Path $root "README.md") $out
Compress-Archive -Path $exe, (Join-Path $out "README.md") -DestinationPath $zip -Force
$hash = Write-Checksum $zip

"Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB, version $productVersion)"
"Zipped $zip"
"SHA-256 $hash"

if (-not $SkipInstaller) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Warning "Inno Setup 6 not found (winget install JRSoftware.InnoSetup); skipping the installer."
    }
    else {
        & $iscc /Q "/DAppVersion=$productVersion" "/DSourceDir=$out" "/O$OutputRoot" (Join-Path $PSScriptRoot "installer.iss")
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
        $setup = Join-Path $OutputRoot "AgentReclaim-$productVersion-$Runtime-setup.exe"
        if ($CertThumbprint) { Sign-File $setup }
        "Installer $setup"
        "SHA-256 $(Write-Checksum $setup)"
    }
}
