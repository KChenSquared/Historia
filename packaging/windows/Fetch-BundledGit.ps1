[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Staging,

    [string]$CacheDir = 'obj/bundled-git-cache',
    [string]$DevOutput = '',
    [string]$ManifestPath = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
if (-not $ManifestPath) {
    $ManifestPath = Join-Path $RepoRoot "packaging/bundled-git.json"
}

if (-not (Test-Path $ManifestPath)) {
    throw "Manifest not found: $ManifestPath"
}

$manifest = Get-Content -Raw $ManifestPath | ConvertFrom-Json

$resolvedCacheDir = if ([System.IO.Path]::IsPathRooted($CacheDir)) {
    $CacheDir
} else {
    Join-Path $RepoRoot $CacheDir
}

if (-not (Test-Path $resolvedCacheDir)) {
    New-Item -ItemType Directory -Path $resolvedCacheDir -Force | Out-Null
}

function Verify-FileHash {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )
    $actualHash = (Get-FileHash -Path $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $(Split-Path -Leaf $FilePath). Expected: $ExpectedSha256, got: $actualHash"
    }
}

function Download-Asset {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )
    if (Test-Path $Destination) {
        try {
            Verify-FileHash -FilePath $Destination -ExpectedSha256 $ExpectedSha256
            Write-Host "Cached $(Split-Path -Leaf $Destination) verified."
            return
        } catch {
            Write-Warning "Cached $(Split-Path -Leaf $Destination) hash verification failed. Re-downloading."
            Remove-Item -Force $Destination
        }
    }

    Write-Host "Downloading $Url to $Destination..."
    Invoke-WebRequest -Uri $Url -OutFile $Destination
    Verify-FileHash -FilePath $Destination -ExpectedSha256 $ExpectedSha256
    Write-Host "Downloaded and verified $(Split-Path -Leaf $Destination)."
}

$mingitCache = Join-Path $resolvedCacheDir $manifest.mingit.asset
Download-Asset -Url $manifest.mingit.url -Destination $mingitCache -ExpectedSha256 $manifest.mingit.sha256

$gitLfsCache = Join-Path $resolvedCacheDir $manifest.gitLfs.asset
Download-Asset -Url $manifest.gitLfs.url -Destination $gitLfsCache -ExpectedSha256 $manifest.gitLfs.sha256

$gitStaging = Join-Path $Staging 'git'
if (Test-Path $gitStaging) {
    Remove-Item -Recurse -Force $gitStaging
}
New-Item -ItemType Directory -Path $gitStaging -Force | Out-Null

Write-Host "Extracting MinGit to $gitStaging..."
Expand-Archive -Path $mingitCache -DestinationPath $gitStaging -Force

$prefix = $null
foreach ($candidate in @('ucrt64', 'mingw64', 'clangarm64')) {
    if (Test-Path (Join-Path $gitStaging "$candidate\bin\git.exe")) {
        $prefix = $candidate
        break
    }
}

if (-not $prefix) {
    throw "Failed to locate git.exe under ucrt64, mingw64, or clangarm64 in $gitStaging"
}

$tempLfsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("git-lfs-extract-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempLfsDir -Force | Out-Null

try {
    Write-Host "Extracting Git LFS..."
    Expand-Archive -Path $gitLfsCache -DestinationPath $tempLfsDir -Force

    $lfsExe = Get-ChildItem -Path $tempLfsDir -Filter "git-lfs.exe" -Recurse | Select-Object -First 1
    if (-not $lfsExe) {
        throw "git-lfs.exe not found in extracted archive: $gitLfsCache"
    }

    $destLfs = Join-Path $gitStaging "$prefix\bin\git-lfs.exe"
    Copy-Item -Path $lfsExe.FullName -Destination $destLfs -Force
} finally {
    if (Test-Path $tempLfsDir) {
        Remove-Item -Recurse -Force $tempLfsDir
    }
}

$cmdGit = Join-Path $gitStaging "cmd\git.exe"
$binGit = Join-Path $gitStaging "$prefix\bin\git.exe"
$binLfs = Join-Path $gitStaging "$prefix\bin\git-lfs.exe"
$systemConfig = Join-Path $gitStaging "etc\gitconfig"

$requiredFiles = @(
    $cmdGit,
    $binGit,
    $binLfs,
    $systemConfig,
    (Join-Path $gitStaging "LICENSE.txt"),
    (Join-Path $gitStaging "etc\package-versions.txt"),
    (Join-Path $gitStaging "usr\share\licenses\gcc-libs\COPYING3"),
    (Join-Path $gitStaging "usr\share\licenses\openssh\LICENCE"),
    (Join-Path $gitStaging "$prefix\doc\git-credential-manager\LICENSE")
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path $required)) { throw "Assertion failed: missing $required" }
}

# Two settings the Git for Windows installer writes and MinGit's config lacks: Git Credential
# Manager cannot sign in to a dev.azure.com URL without the path, and status is slower without the
# file cache.
[System.IO.File]::AppendAllText($systemConfig, "[core]`n`tfscache = true`n[credential `"https://dev.azure.com`"]`n`tuseHttpPath = true`n")

$fscache = & $binGit config --system --get core.fscache
if ($fscache -ne 'true') { throw "Assertion failed: core.fscache is '$fscache' in $systemConfig" }
$useHttpPath = & $binGit config --system --get-urlmatch credential.useHttpPath https://dev.azure.com/org/project/_git/repo
if ($useHttpPath -ne 'true') { throw "Assertion failed: credential.useHttpPath is '$useHttpPath' for dev.azure.com in $systemConfig" }

Write-Host "Running smoke check for bundled git and git-lfs..."
$gitVersionOut = & $cmdGit --version
if ($LASTEXITCODE -ne 0) {
    throw "Smoke check failed: $cmdGit --version exited with code $LASTEXITCODE"
}
Write-Host "Smoke check passed: $gitVersionOut"

$oldPath = $env:PATH
try {
    $env:PATH = "$(Join-Path $gitStaging "$prefix\bin");$(Join-Path $gitStaging 'usr\bin');$env:PATH"
    $lfsVersionOut = & $cmdGit lfs version
    if ($LASTEXITCODE -ne 0) {
        throw "Smoke check failed: $cmdGit lfs version exited with code $LASTEXITCODE"
    }
    Write-Host "Smoke check passed: $lfsVersionOut"
} finally {
    $env:PATH = $oldPath
}

if ($DevOutput) {
    $resolvedDevOutput = if ([System.IO.Path]::IsPathRooted($DevOutput)) {
        $DevOutput
    } else {
        Join-Path $RepoRoot $DevOutput
    }
    $targetGit = Join-Path $resolvedDevOutput "git"
    if (Test-Path $targetGit) {
        Remove-Item -Recurse -Force $targetGit
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $targetGit) -Force | Out-Null
    Copy-Item -Path $gitStaging -Destination $targetGit -Recurse -Force
    Write-Host "Copied bundled git to DevOutput: $targetGit"
}

Write-Host "Fetch-BundledGit completed successfully."

