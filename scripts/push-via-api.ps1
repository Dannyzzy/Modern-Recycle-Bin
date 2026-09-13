# Push the local repository to GitHub through the REST API.
#
# Needed because github.com's HTTPS endpoint is blocked on this network while
# api.github.com is reachable: instead of `git push` we create blobs, a tree and
# a commit over the API, then point the branch at it.
#
# Usage:  $env:GH_TOKEN = '<token>';  .\scripts\push-via-api.ps1 -Owner <user> -Repo <name>
#
# The token needs, at minimum:
#   classic token  : repo, workflow
#   fine-grained   : Contents: Read and write  +  Workflows: Read and write

param(
    [Parameter(Mandatory=$true)][string]$Owner,
    [Parameter(Mandatory=$true)][string]$Repo,
    [string]$Branch = 'main',
    [string]$Message = 'Initial release: Modern Recycle Bin 1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gh   = Join-Path $root '..\tools\gh\bin\gh.exe'
$api  = "repos/$Owner/$Repo"

if (-not (Test-Path $gh)) { throw "gh.exe not found at $gh" }
if (-not $env:GH_TOKEN)   { throw "GH_TOKEN is not set." }

# ---------------------------------------------------------------- files
$files = Get-ChildItem $root -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and
                   $_.FullName -notmatch '\\dist\\' -and
                   $_.FullName -notmatch '\\build\\' -and
                   $_.FullName -notmatch '\\tools\\' }
Write-Host "Uploading $($files.Count) files to $Owner/$Repo ..."

# ---------------------------------------------------------------- blobs
$tree = @()
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $b64 = [Convert]::ToBase64String($bytes)

    $payload = @{ content = $b64; encoding = 'base64' } | ConvertTo-Json -Compress
    $tmp = Join-Path $env:TEMP ("mrb-blob-" + [guid]::NewGuid().ToString('N') + ".json")
    [System.IO.File]::WriteAllText($tmp, $payload, (New-Object System.Text.UTF8Encoding($false)))

    $res = & $gh api --method POST "$api/git/blobs" --input $tmp 2>&1 | Out-String
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue

    $sha = $null
    if ($res -match '"sha"\s*:\s*"([0-9a-f]{40})"') { $sha = $Matches[1] }
    if (-not $sha) { throw "blob upload failed for $rel : $res" }

    $tree += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $sha }
    Write-Host ("  + {0}" -f $rel)
}

# ---------------------------------------------------------------- tree
$treePayload = @{ tree = $tree } | ConvertTo-Json -Depth 6 -Compress
$treeTmp = Join-Path $env:TEMP ("mrb-tree-" + [guid]::NewGuid().ToString('N') + ".json")
[System.IO.File]::WriteAllText($treeTmp, $treePayload, (New-Object System.Text.UTF8Encoding($false)))
$treeRes = & $gh api --method POST "$api/git/trees" --input $treeTmp 2>&1 | Out-String
Remove-Item $treeTmp -Force -ErrorAction SilentlyContinue
if ($treeRes -notmatch '"sha"\s*:\s*"([0-9a-f]{40})"') { throw "tree creation failed: $treeRes" }
$treeSha = $Matches[1]
Write-Host "tree: $treeSha"

# ------------------------------------------------------------- commit
$commitPayload = @{ message = $Message; tree = $treeSha } | ConvertTo-Json -Compress
$commitTmp = Join-Path $env:TEMP ("mrb-commit-" + [guid]::NewGuid().ToString('N') + ".json")
[System.IO.File]::WriteAllText($commitTmp, $commitPayload, (New-Object System.Text.UTF8Encoding($false)))
$commitRes = & $gh api --method POST "$api/git/commits" --input $commitTmp 2>&1 | Out-String
Remove-Item $commitTmp -Force -ErrorAction SilentlyContinue
if ($commitRes -notmatch '"sha"\s*:\s*"([0-9a-f]{40})"') { throw "commit creation failed: $commitRes" }
$commitSha = $Matches[1]
Write-Host "commit: $commitSha"

# ---------------------------------------------------------------- ref
$refRes = & $gh api "$api/git/ref/heads/$Branch" 2>&1 | Out-String
if ($refRes -match '"ref"') {
    $refPayload = @{ sha = $commitSha; force = $true } | ConvertTo-Json -Compress
    $method = 'PATCH'
    $target = "$api/git/refs/heads/$Branch"
} else {
    $refPayload = @{ ref = "refs/heads/$Branch"; sha = $commitSha } | ConvertTo-Json -Compress
    $method = 'POST'
    $target = "$api/git/refs"
}
$refTmp = Join-Path $env:TEMP ("mrb-ref-" + [guid]::NewGuid().ToString('N') + ".json")
[System.IO.File]::WriteAllText($refTmp, $refPayload, (New-Object System.Text.UTF8Encoding($false)))
$out = & $gh api --method $method $target --input $refTmp 2>&1 | Out-String
Remove-Item $refTmp -Force -ErrorAction SilentlyContinue
Write-Host $out

Write-Host ""
Write-Host "Pushed. https://github.com/$Owner/$Repo"
