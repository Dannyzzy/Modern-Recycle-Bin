# Correct GitHub status check (the earlier inline test mis-matched
# "not logged into" because -match is case-insensitive).
$root = Split-Path -Parent $PSScriptRoot
$gh   = Join-Path $root '..\tools\gh\bin\gh.exe'

$out = & $gh auth status 2>&1 | Out-String
if ($out -match 'Logged in to') {
    Write-Host 'AUTH: yes'
    $out.Trim() | Write-Host
} else {
    Write-Host 'AUTH: no'
}

foreach ($u in @('https://github.com','https://api.github.com')) {
    $code = & curl.exe -s -o NUL -w "%{http_code}" --max-time 10 $u 2>$null
    if ($code -and $code -ne '000') { Write-Host "$u -> HTTP $code" } else { Write-Host "$u -> unreachable" }
}