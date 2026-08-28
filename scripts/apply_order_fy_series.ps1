# DoodhDirect — Apply financial-year numbering to the live Development ORDER series
# Uses the audited service UpdateAsync (PUT /number-series/ORDER?scope=MAIN).
# Both ORDER rows are never-used (LastUsedNumber=0), so EnsureSafeEdit permits the edit
# and the counter (StartingNumber/LastUsedNumber) is NOT touched.
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5209'

$loginBody = @{
    login    = 'owner@doodhdirect.local'
    password = 'DoodhDirect@123'
    device   = @{
        deviceIdentifier = 'migration-update-20260828'
        deviceName       = 'Migration Update'
        platform         = 'CLI'
    }
} | ConvertTo-Json -Depth 4

$login = Invoke-RestMethod -Uri "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body $loginBody
$token = $login.data.tokens.accessToken
$headers = @{ Authorization = "Bearer $token" }

# --- Scoped ORDER (MAIN): legacy ORD/MAIN/{NUMBER:000000} + Never -> FY template + FinancialYear ---
$body = @{
    description   = 'One-time and subscription order numbers for branch MAIN'
    template      = 'ORD/MAIN/{FY}/{NUMBER:000000}'
    startingNumber = 1
    incrementBy   = 1
    resetPolicy   = 'FinancialYear'
} | ConvertTo-Json

$resp = Invoke-RestMethod -Uri "$base/api/v1/admin/setup/number-series/ORDER?scope=MAIN" -Method Put -Headers $headers -ContentType 'application/json' -Body $body
$d = $resp.data
Write-Host ("UPDATED ORDER@{0} template={1} policy={2} starting={3} lastUsed={4} active={5}" -f $d.scopeKey, $d.template, $d.resetPolicy, $d.startingNumber, $d.lastUsedNumber, $d.isActive)
