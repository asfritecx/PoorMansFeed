#requires -Version 7.0
# Publishes PoorMansFeed as a self-contained single-file Release build.
#
#   pwsh -NoProfile -File build/publish.ps1 [-Runtime all|win-x64|osx-arm64]     (default: all)
#
# Output:
#   artifacts/win-x64/PoorMansFeed.exe
#   artifacts/osx-arm64/PoorMansFeed.app      (assembled on macOS only; without macOS the bare executable
#                                                  is left in artifacts/osx-arm64/PoorMansFeed)
# Restore runs once in locked mode; the publishes use --no-restore.

[CmdletBinding()]
param(
    [ValidateSet('all', 'win-x64', 'osx-arm64')]
    [string] $Runtime = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/PoorMansFeed/PoorMansFeed.csproj'
$assets = Join-Path $root 'src/PoorMansFeed/Assets'
$artifacts = Join-Path $root 'artifacts'
$staging = Join-Path $artifacts '.staging'
$appName = 'PoorMansFeed'
$bundleId = 'io.github.asfritecx.poormansfeed'
$minMacOs = '14.0'
$runtimes = if ($Runtime -eq 'all') { @('win-x64', 'osx-arm64') } else { @($Runtime) }

function Invoke-Checked {
    param([Parameter(Mandatory)] [string] $FilePath, [Parameter(Mandatory)] [string[]] $ArgumentList)
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "'$FilePath' failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Project not found: $project" }
if (-not (Test-Path -LiteralPath (Join-Path $assets "$appName.ico") -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $assets "$appName.icns") -PathType Leaf)) {
    throw "Icons missing from $assets"
}

# <Version> in Directory.Build.props is the one version source.
$version = (& dotnet msbuild $project -nologo -getProperty:Version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Could not read a numeric major.minor.patch <Version> (got '$version')."
}

Write-Host 'Restoring (locked mode)...'
Invoke-Checked -FilePath 'dotnet' -ArgumentList @('restore', $project, '--locked-mode')

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$results = @()

foreach ($rid in $runtimes) {
    Write-Host "Publishing $rid..."
    $stageDir = Join-Path $staging $rid
    $outDir = Join-Path $artifacts $rid
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    Invoke-Checked -FilePath 'dotnet' -ArgumentList @(
        'publish', $project,
        '--configuration', 'Release',
        '--runtime', $rid,
        '--self-contained', 'true',
        '--no-restore',
        '--output', $stageDir,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:DebugType=embedded'
    )

    # Symbols are embedded in the single file; drop any stray PDB or createdump helper.
    Get-ChildItem -LiteralPath $stageDir -Recurse -Filter '*.pdb' | Remove-Item -Force
    Get-ChildItem -LiteralPath $stageDir -Filter 'createdump*' | Remove-Item -Force

    $exeName = if ($rid -eq 'win-x64') { "$appName.exe" } else { $appName }
    $exe = Join-Path $stageDir $exeName
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "The app host was not produced at '$exe'." }
    $files = @(Get-ChildItem -LiteralPath $stageDir -File -Recurse)
    if ($files.Count -ne 1) {
        throw "Expected one file for $rid but found $($files.Count): $($files.FullName -join ', ')"
    }

    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    if ($rid -eq 'win-x64' -or -not $IsMacOS) {
        if ($rid -ne 'win-x64') {
            Write-Warning 'The .app bundle is assembled on macOS only. The bare executable is kept.'
        }
        Move-Item -LiteralPath $exe -Destination (Join-Path $outDir $exeName)
        $results += (Join-Path $outDir $exeName)
        continue
    }

    # macOS bundle: the single-file executable, the .icns, and an Info.plist with the build version.
    $bundle = Join-Path $outDir "$appName.app"
    $contents = Join-Path $bundle 'Contents'
    $macOs = Join-Path $contents 'MacOS'
    $resources = Join-Path $contents 'Resources'
    New-Item -ItemType Directory -Path $macOs -Force | Out-Null
    New-Item -ItemType Directory -Path $resources -Force | Out-Null
    Move-Item -LiteralPath $exe -Destination (Join-Path $macOs $appName)
    Invoke-Checked -FilePath 'chmod' -ArgumentList @('+x', (Join-Path $macOs $appName))
    Copy-Item -LiteralPath (Join-Path $assets "$appName.icns") -Destination (Join-Path $resources "$appName.icns")

    # CFBundleName is limited to 15 characters, which the app name fits.
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>$appName</string>
  <key>CFBundleIconFile</key>
  <string>$appName.icns</string>
  <key>CFBundleExecutable</key>
  <string>$appName</string>
  <key>CFBundleIdentifier</key>
  <string>$bundleId</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$appName</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>LSMinimumSystemVersion</key>
  <string>$minMacOs</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
"@
    $plistPath = Join-Path $contents 'Info.plist'
    Set-Content -LiteralPath $plistPath -Value $plist -Encoding utf8NoBOM
    Invoke-Checked -FilePath 'plutil' -ArgumentList @('-lint', $plistPath)

    # Ad-hoc signing only (no Developer ID, no notarisation).
    Invoke-Checked -FilePath 'codesign' -ArgumentList @('--force', '--deep', '--sign', '-', $bundle)
    Invoke-Checked -FilePath 'codesign' -ArgumentList @('--verify', '--deep', '--strict', '--verbose=2', $bundle)
    $results += (Join-Path $macOs $appName)
    $results += $bundle
}

if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

Write-Host ''
Write-Host 'Created:'
foreach ($path in $results) {
    if (Test-Path -LiteralPath $path -PathType Container) {
        Write-Host "  $path  (bundle, version $version)"
    } else {
        $size = (Get-Item -LiteralPath $path).Length
        Write-Host ('  {0}  {1} bytes ({2:N1} MB)' -f $path, $size, ($size / 1MB))
    }
}
