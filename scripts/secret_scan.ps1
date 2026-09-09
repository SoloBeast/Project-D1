# Secret scan: search working tree (excluding bin/obj/.git/build/node_modules)
# for likely hardcoded credentials, focusing on MSG91 and general secrets.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$patterns = @(
    '(?i)(authkey|auth_key|auth-key|api_key|api-key|apiKey|widget_id|widget-id|widgetId|signing_key|signing-key|signingKey)\s*[:=]\s*["''][A-Za-z0-9+/=_\-]{16,}["'']',
    '(?i)(password|passwd|pwd)\s*[:=]\s*["''][^"'']{8,}["'']',
    '(?i)(secret|client_secret)\s*[:=]\s*["''][A-Za-z0-9+/=_\-]{16,}["'']',
    '(?i)-----BEGIN [A-Z ]*PRIVATE KEY-----',
    '(?i)(access_token|refresh_token)\s*[:=]\s*["''][A-Za-z0-9._\-]{24,}["'']'
)

$files = Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch '\\bin\\|\\obj\\|\\.git\\|\\build\\|\\node_modules\\|\\\.dart_tool\\|\\test\\golden'
    }

$hits = @()
foreach ($file in $files) {
    $lineNo = 0
    try {
        Get-Content -LiteralPath $file.FullName -ErrorAction Stop | ForEach-Object {
            $lineNo++
            foreach ($p in $patterns) {
                if ($_ -match $p) {
                    $hits += [pscustomobject]@{
                        File    = $file.FullName.Substring($root.Length + 1)
                        Line    = $lineNo
                        Content = ($_ -replace '\s+', ' ').Substring(0, [Math]::Min(160, ($_ -replace '\s+', ' ').Length))
                    }
                    break
                }
            }
        }
    } catch {
        # Binary or unreadable file - skip
    }
}

if ($hits.Count -eq 0) {
    Write-Output "SECRET SCAN: OK - no hardcoded secrets detected."
    exit 0
}

Write-Output "SECRET SCAN: POTENTIAL SECRETS FOUND ($($hits.Count))"
$hits | Format-Table -AutoSize | Out-String -Width 200
exit 1
