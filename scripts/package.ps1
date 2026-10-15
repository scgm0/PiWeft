[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Rid,
    [string]$Version = '0.1.0',
    [string]$Output = 'artifacts',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Version must be a semantic version without a v prefix.' }
$repo = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath($Output, $repo)
$publish = Join-Path $outputRoot "publish/$Rid"
$stage = Join-Path $outputRoot "stage/PiWeft-$Version-$Rid"
if (Test-Path $stage) { throw "Staging directory already exists: $stage. Choose a fresh Output directory." }

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

if (-not $SkipPublish) {
    foreach ($project in @('PiWeft.Cli', 'PiWeft.Desktop')) {
        Invoke-Dotnet @('publish', (Join-Path $repo "src/$project/$project.csproj"), '-c', 'Release', '-r', $Rid,
            '--self-contained', 'true', '-p:PublishAot=true', "-p:Version=$Version", '-o', (Join-Path $publish $project))
    }
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null
$extension = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
# CLI 的构建产物就是 pi；桌面端是 piweft-desktop。
foreach ($item in @(@('PiWeft.Cli', "pi$extension"), @('PiWeft.Desktop', "piweft-desktop$extension"))) {
    $source = Join-Path $publish "$($item[0])/$($item[1])"
    if (-not (Test-Path $source)) { throw "Missing published binary: $source" }
    Copy-Item $source $stage
}
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Get-ChildItem (Join-Path $repo 'src/PiWeft.Desktop/Resources') -Recurse -Filter 'LICENSE-*.txt' | Copy-Item -Destination $licenses
Copy-Item (Join-Path $repo 'README.md') $stage

if ($Rid.StartsWith('osx-')) {
    $bundle = Join-Path $stage 'PiWeft Desktop.app/Contents'
    New-Item -ItemType Directory -Path (Join-Path $bundle 'MacOS') -Force | Out-Null
    Move-Item (Join-Path $stage 'piweft-desktop') (Join-Path $bundle 'MacOS/piweft-desktop')
    $bundleVersion = ($Version -split '[-+]')[0]
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>dev.piweft.desktop</string>
<key>CFBundleName</key><string>PiWeft Desktop</string>
<key>CFBundleExecutable</key><string>piweft-desktop</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>$bundleVersion</string>
<key>CFBundleShortVersionString</key><string>$bundleVersion</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
    [IO.File]::WriteAllText((Join-Path $bundle 'Info.plist'), $plist, [Text.UTF8Encoding]::new($false))
    & chmod +x (Join-Path $bundle 'MacOS/piweft-desktop') (Join-Path $stage 'pi')
    if ($LASTEXITCODE -ne 0) { throw 'chmod failed' }
    # Ad-hoc signing makes the bundle locally runnable; notarization requires maintainer credentials.
    & codesign --force --deep --sign - (Join-Path $stage 'PiWeft Desktop.app')
    if ($LASTEXITCODE -ne 0) { throw 'codesign failed' }
} elseif (-not $Rid.StartsWith('win-')) {
    & chmod +x (Join-Path $stage 'pi') (Join-Path $stage 'piweft-desktop')
    if ($LASTEXITCODE -ne 0) { throw 'chmod failed' }
}

$packageRoot = Join-Path $outputRoot 'packages'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
$name = Split-Path $stage -Leaf
if ($Rid.StartsWith('win-')) {
    $archive = Join-Path $packageRoot "$name.zip"
    Compress-Archive -Path $stage -DestinationPath $archive
} else {
    $archive = Join-Path $packageRoot "$name.tar.gz"
    & tar -czf $archive -C (Split-Path $stage -Parent) $name
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
}
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$hash  $(Split-Path $archive -Leaf)`n", [Text.UTF8Encoding]::new($false))
Write-Host "Package: $archive"
