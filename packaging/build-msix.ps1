<#
.SYNOPSIS
  Builds an MSIX package of MyVideoEditor, FFmpeg included (Windows only; needs the Windows
  10/11 SDK for makeappx.exe).

.EXAMPLE
  # Store submission (unsigned; the Store signs it). Use the identity from Partner Center.
  ./packaging/build-msix.ps1 -Version 1.0.0.0 -IdentityName 12345YourName.MyVideoEditor `
      -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "Your Name"

.EXAMPLE
  # Local sideload test, signed with a self-signed certificate created on the fly.
  ./packaging/build-msix.ps1 -SelfSign
#>
param(
    [string]$Version = "0.1.0.0",
    [ValidateSet("x64", "arm64")]
    [string[]]$Architecture = @("x64"),
    [string]$IdentityName = "myvideoeditor.dev",
    [string]$Publisher = "CN=myvideoeditor-dev",
    [string]$PublisherDisplayName = "MyVideoEditor developer",
    [string]$OutputDir = "artifacts/msix",
    [string]$FfmpegSeries = "8.1",
    [switch]$SelfSign
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue" # Invoke-WebRequest is very slow with the progress bar
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') {
    throw "Version must look like 1.2.3.0 (the Store requires the last part to be 0)."
}

function Find-SdkTool([string]$name) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $tool = Get-ChildItem $kits -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found. Install the Windows 10/11 SDK." }
    return $tool.FullName
}

# FFmpeg: a GPL shared build from BtbN/FFmpeg-Builds (see tools/fetch-ffmpeg.sh for the Linux side),
# put in <layout>\ffmpeg\bin with its DLLs, plus its licence and where its source is.
function Get-Ffmpeg([string]$arch, [string]$destination) {
    $name = if ($arch -eq "arm64") { "winarm64" } else { "win64" }
    $file = "ffmpeg-n$FfmpegSeries-latest-$name-gpl-shared-$FfmpegSeries"
    $url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$file.zip"
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        Write-Host "Downloading $url"
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $work "ffmpeg.zip")
        Expand-Archive (Join-Path $work "ffmpeg.zip") -DestinationPath $work
        $extracted = Join-Path $work $file
        New-Item -ItemType Directory -Force -Path (Join-Path $destination "bin") | Out-Null
        Copy-Item (Join-Path $extracted "bin\ffmpeg.exe"), (Join-Path $extracted "bin\ffprobe.exe") (Join-Path $destination "bin")
        Copy-Item (Join-Path $extracted "bin\*.dll") (Join-Path $destination "bin")
        Copy-Item (Join-Path $extracted "LICENSE.txt") (Join-Path $destination "FFMPEG-LICENSE.txt")
        @"
This folder contains ffmpeg, ffprobe and their libraries from FFmpeg (https://ffmpeg.org), licensed
under the GNU General Public License version 3 (see FFMPEG-LICENSE.txt). MyVideoEditor runs them
as separate programs; MyVideoEditor itself is MIT-licensed.

Build:  $file
        from $url
        built by https://github.com/BtbN/FFmpeg-Builds (build scripts and the exact
        configuration of every bundled library are in that repository)
Source: FFmpeg release branch release/$FfmpegSeries at https://github.com/FFmpeg/FFmpeg/tree/release/$FfmpegSeries
        (also https://ffmpeg.org/download.html#releases)

You may obtain the complete corresponding source code of this FFmpeg build, including the
libraries it was built with, from the locations above. If they are unavailable, ask via
https://github.com/StuMP90/myvideoeditor/issues and it will be provided, for at least three
years after this release.
"@ | Set-Content -Encoding UTF8 (Join-Path $destination "FFMPEG-SOURCE.txt")
    }
    finally {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

$makeappx = Find-SdkTool "makeappx.exe"
New-Item -ItemType Directory -Force -Path (Join-Path $root $OutputDir) | Out-Null
$packages = @()

foreach ($arch in $Architecture) {
    $layout = Join-Path $root "artifacts/msix-layout/$arch"
    if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }

    Write-Host "Publishing win-$arch..."
    dotnet publish (Join-Path $root "src/MyVideoEditor.App/MyVideoEditor.App.csproj") -c Release -r "win-$arch" --self-contained true `
        -p:Version=$($Version -replace '\.0$', '') -o $layout
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    Get-Ffmpeg $arch (Join-Path $layout "ffmpeg")
    & (Join-Path $layout "ffmpeg\bin\ffmpeg.exe") -hide_banner -version | Select-Object -First 1

    # Licence texts for everything shipped (shown in the app under Help → Licences).
    # "python" first: on Windows "python3" can be the Microsoft Store placeholder.
    $python = Get-Command python, py, python3 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $python) { throw "Python 3 is needed to generate THIRD-PARTY-NOTICES.txt." }
    & $python.Source (Join-Path $root "tools/generate-third-party-notices.py") --rid "win-$arch" --ffmpeg (Join-Path $layout "ffmpeg") --output (Join-Path $layout "THIRD-PARTY-NOTICES.txt")
    if ($LASTEXITCODE -ne 0) { throw "Generating third-party notices failed" }
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $layout "LICENSE.txt")

    Copy-Item -Recurse (Join-Path $root "packaging/msix/Images") (Join-Path $layout "Images")
    (Get-Content (Join-Path $root "packaging/msix/AppxManifest.xml") -Raw) `
        -replace '\$\(IdentityName\)', $IdentityName `
        -replace '\$\(Publisher\)', $Publisher `
        -replace '\$\(PublisherDisplayName\)', $PublisherDisplayName `
        -replace '\$\(Version\)', $Version `
        -replace '\$\(Architecture\)', $arch |
        Set-Content -Encoding UTF8 (Join-Path $layout "AppxManifest.xml")

    $msix = Join-Path $root "$OutputDir/MyVideoEditor_${Version}_$arch.msix"
    & $makeappx pack /o /d $layout /p $msix
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
    $packages += $msix
    Write-Host "Created $msix"
}

if ($SelfSign) {
    $signtool = Find-SdkTool "signtool.exe"
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "MyVideoEditor dev signing" -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    foreach ($msix in $packages) {
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
        if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
    }
    Write-Host ""
    Write-Host "Signed with self-signed '$Publisher'. To install locally, trust it once (admin PowerShell):"
    Write-Host "  Export-Certificate -Cert Cert:\CurrentUser\My\$($cert.Thumbprint) -FilePath myvideoeditor-dev.cer"
    Write-Host "  Import-Certificate -FilePath myvideoeditor-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}
