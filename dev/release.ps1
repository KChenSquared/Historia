[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Message = '',
    [string]$Remote = 'origin'
)

# Tags HEAD and pushes the tag, which starts the Package workflow. When HEAD contains
# docs/release-notes/<tag>.md the draft release starts from it; otherwise the draft release has a
# blank body. Only the tag is pushed, after a confirmation; no branch is pushed.

$ErrorActionPreference = 'Stop'

# Runs git with each argument passed as its own element and returns its output lines. Throws on
# a non-zero exit code, because native exit codes do not trip $ErrorActionPreference.
function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = & git -C $RepoRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') exited with code $LASTEXITCODE"
    }
    return $output
}

# Returns $true when git exits with 0; used for questions where a failure is an answer.
function Test-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    # Windows PowerShell 5.1 turns redirected native stderr into a terminating error under 'Stop'.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & git -C $RepoRoot @Arguments 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

if ($Tag -notmatch '^v\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$') {
    throw "Tag '$Tag' is not a release tag. Use v<major>.<minor>.<patch>, optionally followed by a pre-release suffix such as -rc.1."
}

$RepoRoot = Split-Path -Parent $PSScriptRoot

$status = Invoke-Git -Arguments @('status', '--porcelain')
if ($status) {
    throw "The working tree is not clean. Commit or stash your changes before releasing."
}

$notesPath = "docs/release-notes/$Tag.md"
if (-not (Test-Git -Arguments @('cat-file', '-e', "HEAD:$notesPath"))) {
    Write-Warning "HEAD has no $notesPath, so the draft release will have a blank body. Fill it in on GitHub, or commit the notes file to main later to sync it."
}

if (Test-Git -Arguments @('rev-parse', '--verify', '--quiet', "refs/tags/$Tag")) {
    throw "Tag $Tag already exists locally."
}

if (-not (Test-Git -Arguments @('remote', 'get-url', $Remote))) {
    throw "Remote '$Remote' does not exist."
}

$head = (Invoke-Git -Arguments @('rev-parse', '--short', 'HEAD')) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($Message)) {
    $Message = "Historia $Tag"
}

Write-Host "Creating annotated tag $Tag at $head ($Message)."
Invoke-Git -Arguments @('tag', '-a', $Tag, '-m', $Message) | Out-Null

$answer = Read-Host "Push tag $Tag to '$Remote'? This starts the release build. Type 'yes' to continue"
if ($answer -ne 'yes') {
    Write-Host "Not pushed. Tag $Tag exists locally; delete it with: git tag -d $Tag"
    exit 1
}

Invoke-Git -Arguments @('push', $Remote, "refs/tags/$Tag") | Out-Null
Write-Host "Pushed $Tag to '$Remote'. The Package workflow creates the draft release."
