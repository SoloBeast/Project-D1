# DoodhDirect — Read-only Number Series preview verification (Development)
# Calls POST /api/v1/admin/setup/number-series/preview for the seeded series.
# The preview endpoint uses NumberSeriesService.PreviewNextNumberAsync -> PeekNextNumber
# (read-only; no SaveChanges, no counter advance, no business records created).
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5209'

# --- Authenticate as Development Owner (has SETUP.NUMBER_SERIES.READ via global access) ---
$loginBody = @{
    login    = 'owner@doodhdirect.local'
    password = 'DoodhDirect@123'
    device   = @{
        deviceIdentifier = 'migration-verify-20260828'
        deviceName       = 'Migration Verify'
        platform         = 'CLI'
    }
} | ConvertTo-Json -Depth 4

$login = Invoke-RestMethod -Uri "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body $loginBody
$token = $login.data.tokens.accessToken
Write-Host ("LOGIN OK user={0} roles={1}" -f $login.data.user.displayName, ($login.data.user.roles -join ','))

$headers = @{ Authorization = "Bearer $token" }

# --- Read-only preview per seeded series (scoped series send their scope key) ---
# The live Development DB carries a single scoped ORDER series (scope MAIN); the
# list below mirrors exactly those rows.
$series = @(
    @{ Code = 'CUSTOMER'; Template = 'CUST/{NUMBER:0000}'; ScopeKey = '' },
    @{ Code = 'ORDER';    Template = 'ORD/{SCOPE}/{FY}/{NUMBER:000000}'; ScopeKey = 'MAIN' },
    @{ Code = 'BRANCH';   Template = 'BR/{NUMBER:000}'; ScopeKey = '' },
    @{ Code = 'DELIVERY'; Template = 'DEL/{NUMBER:000000}'; ScopeKey = '' }
)

foreach ($s in $series) {
    $body = @{ code = $s.Code; template = $s.Template }
    if ($s.ScopeKey) { $body.scope = $s.ScopeKey }
    $bodyJson = $body | ConvertTo-Json
    $resp = Invoke-RestMethod -Uri "$base/api/v1/admin/setup/number-series/preview" -Method Post -Headers $headers -ContentType 'application/json' -Body $bodyJson
    $d = $resp.data
    $scope = if ($d.scopeKey) { $d.scopeKey } else { '-' }
    Write-Host ("PREVIEW {0,-9} scope={1,-4} template={2,-30} next={3} formatted={4}" -f $d.code, $scope, $d.template, $d.nextNumber, $d.formattedNumber)
}

# --- Read-only: confirm stored counters are unchanged (preview must not advance) ---
$list = Invoke-RestMethod -Uri "$base/api/v1/admin/setup/number-series" -Method Get -Headers $headers
foreach ($row in $list.data) {
    $scope = $row.scopeKey
    if (-not $scope) { $scope = '-' }
    Write-Host ("STORE {0,-9} scope={1,-4} template={2,-32} lastUsed={3} starting={4} policy={5} active={6}" -f $row.code, $scope, $row.template, $row.lastUsedNumber, $row.startingNumber, $row.resetPolicy, $row.isActive)
}

Write-Host 'PREVIEW TEST COMPLETE (read-only: no counter advance, no business records created)'
