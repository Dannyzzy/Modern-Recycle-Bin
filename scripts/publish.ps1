# Publishing helper (used by the assistant after GitHub sign-in)
#
# Creates the public repository, pushes the current commit and publishes the
# v1.0.0 release with the installer and the portable zip attached.
#
# Run it from the repository root, or let the assistant run it.
# Requires: gh signed in (Setup-GitHub.cmd or Login-With-Token.cmd).

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$gh   = Join-Path $root '..\tools\gh\bin\gh.exe'

$repo   = 'Modern-Recycle-Bin'
$desc   = 'A modern Recycle Bin for Windows 11 - restore anywhere, copy files out, image previews. Built with WebView2 + HTML/CSS.'
$topics = 'windows,recycle-bin,webview2,winforms,windows-11,fluent-design,file-manager,dotnet-framework'

if (-not (Test-Path $gh)) { Write-Host '[x] gh.exe not found'; exit 1 }

Write-Host '== auth =='
& $gh auth status
if ($LASTEXITCODE -ne 0) { Write-Host '[x] not signed in - run Login-GitHub.cmd first'; exit 1 }

$owner = (& $gh api user --jq .login 2>$null)
Write-Host "== owner: $owner =="

Write-Host '== create repository (idempotent) =='
& $gh repo view "$owner/$repo" 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    & $gh repo create $repo --public --description $desc
} else {
    Write-Host '   already exists'
}

Write-Host '== push =='
Push-Location $root
& git remote remove origin 2>$null | Out-Null
# SSH is used deliberately: on this network github.com over HTTPS is blocked
# while the SSH channel (via ssh.github.com:443) stays reachable.
& git remote add origin "git@github.com:$owner/$repo.git"
# github.com is intermittently unreachable from this network - retry.
for ($i = 1; $i -le 6; $i++) {
    & git push -u origin HEAD 2>&1 | Write-Host
    if ($LASTEXITCODE -eq 0) { break }
    Write-Host "   push attempt $i failed, retrying in 5s..."
    Start-Sleep -Seconds 5
}
Pop-Location

Write-Host '== topics =='
$topicArgs = @()
foreach ($t in $topics.Split(',')) { $topicArgs += '--add-topic'; $topicArgs += $t }
& $gh repo edit "$owner/$repo" @topicArgs

Write-Host '== release =='
& $gh release create v1.0.0 `
    (Join-Path $root 'dist\ModernRecycleBinSetup.exe') `
    (Join-Path $root 'dist\ModernRecycleBin-portable.zip') `
    --title 'Modern Recycle Bin 1.0.0' `
    --notes-file (Join-Path $root 'docs\RELEASE-NOTES-v1.0.0.md')

Write-Host '== done =='
& $gh repo view "$owner/$repo" --web
