# Live UAT: availability + dashboard for DABUA (branch 6) and NIT3 (branch 5, archived => API rejects, expected 403/404)
$ErrorActionPreference = "Stop"
$base = "http://localhost:5209"

function Invoke-Login {
    param([string]$Login, [string]$Password)
    $body = @{
        login    = $Login
        password = $Password
        device   = @{
            deviceIdentifier = "uat-ps1"
            deviceName       = "PowerShell UAT"
            platform         = "powershell"
        }
    } | ConvertTo-Json -Depth 5
    $resp = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType "application/json" -Body $body
    if ($null -eq $resp.data.tokens.accessToken) { throw "Login succeeded but no accessToken in response" }
    return $resp.data
}

function Invoke-Get {
    param([string]$Token, [string]$Path)
    $headers = @{ Authorization = "Bearer $Token" }
    try {
        $resp = Invoke-RestMethod -Method Get -Uri "$base$Path" -Headers $headers
        return [pscustomobject]@{ Path = $Path; Http = 200; Data = ($resp.data | ConvertTo-Json -Depth 8 -Compress) }
    }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        $errBody = $_.ErrorDetails.Message
        return [pscustomobject]@{ Path = $Path; Http = $status; Data = $errBody }
    }
}

$session = Invoke-Login -Login "919354816929" -Password "DoodhDirect@123"
$token = $session.tokens.accessToken
$branch = $session.user.branchDetails | Select-Object -First 1
Write-Host "== LOGIN OK =="
Write-Host ("  user  : {0} ({1})" -f $session.user.displayName, ($session.user.roles -join ","))
Write-Host ("  branch: {0} {1} (id={2})" -f $branch.code, $branch.name, $branch.id)
Write-Host ""

# DABUA active branch 6
$avail6   = Invoke-Get -Token $token -Path "/api/v1/dairy/branches/6/availability"
$dash6    = Invoke-Get -Token $token -Path "/api/v1/dairy/branches/6/dashboard"
# NIT3 archived branch 5 - expect rejection
$avail5   = Invoke-Get -Token $token -Path "/api/v1/dairy/branches/5/availability"

Write-Host "== DABUA (branch 6) =="
Write-Host ("  /availability  HTTP {0} -> {1}" -f $avail6.Http, $avail6.Data)
Write-Host ("  /dashboard     HTTP {0} -> {1}" -f $dash6.Http, $dash6.Data)
Write-Host ""
Write-Host "== NIT3 (branch 5, archived) =="
Write-Host ("  /availability  HTTP {0} -> {1}" -f $avail5.Http, $avail5.Data)

$json = @{
    login   = $session.user
    dabua   = @{ availability = $avail6; dashboard = $dash6 }
    nit3    = @{ availability = $avail5 }
} | ConvertTo-Json -Depth 12
$json | Out-File -FilePath "scripts/uat-availability-result.json" -Encoding utf8
Write-Host ""
Write-Host "Saved full result to scripts/uat-availability-result.json"
