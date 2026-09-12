# ============================================================================
# verify-order-000006.ps1
#
# End-to-end verification of the DB0001 ORDER number-series counter correction
# (Series Id 43, LastUsedNumber 0 -> 5).
#
# Flow:
#   1) Login as the development customer (customer@doodhdirect.local /
#      DoodhDirect@123) - a seeded account with a known password.
#   2) checkout-preview with a manual address at the DB0001 Dabua coordinates
#      (28.376674, 77.278628) so distance-based branch allocation picks branch
#      6 (DB0001). The dev customer's saved address is Bengaluru (MAIN), so a
#      manual address is required to target DB0001.
#   3) Create ONE order via POST /api/v1/orders with a fresh Idempotency-Key.
#      Expected OrderNumber = ORD/DB0001/26-27/000006.
#
# No schema/migration/app-code changes. Only a normal order insertion.
# ============================================================================

$ErrorActionPreference = "Stop"
$base = "http://localhost:5209"

# ---- 1) Login as the seeded development customer ---------------------------
$loginBody = @{
    login    = "customer@doodhdirect.local"
    password = "DoodhDirect@123"
    device   = @{ deviceIdentifier = "verify-order-000006"; deviceName = "PowerShell UAT"; platform = "powershell" }
} | ConvertTo-Json -Depth 5

$login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType "application/json" -Body $loginBody
if (-not $login.success) { throw "Login failed: $($login | ConvertTo-Json -Compress)" }
$token = $login.data.tokens.accessToken
$headers = @{ Authorization = "Bearer $token" }
Write-Host ("LOGIN OK: {0} ({1})" -f $login.data.user.displayName, $login.data.user.mobile) -ForegroundColor Green

# ---- 2) checkout-preview with a DB0001-adjacent manual address -------------
$checkoutBody = @{
    manualAddress = @{
        label                = "UAT DB0001 Dabua"
        addressLine1         = "Dabua"
        locality             = "Dabua"
        city                 = "Faridabad"
        state                = "Haryana"
        pinCode              = "121001"
        contactName          = "Development Customer"
        contactMobile        = "9000000000"
        latitude             = 28.376674
        longitude            = 77.278628
        deliveryInstructions = "Number series verification"
    }
    items = @(
        @{ productId = "18D40A17-752A-48DA-A8AB-E577A5C4401E"; quantity = 1 }  # MLK002 Buffalo Milk
    )
} | ConvertTo-Json -Depth 6

$preview = Invoke-RestMethod -Method Post -Uri "$base/api/v1/orders/checkout-preview" -Headers $headers -ContentType "application/json" -Body $checkoutBody
Write-Host ("PREVIEW: {0}" -f ($preview.data | ConvertTo-Json -Depth 6 -Compress))

# ---- 3) Create ONE order with a fresh idempotency key ----------------------
$idempotencyKey = 'verify-000006-' + [Guid]::NewGuid().ToString('N')
$createHeaders = @{ Authorization = "Bearer $token"; 'Idempotency-Key' = $idempotencyKey }

$created = Invoke-RestMethod -Method Post -Uri "$base/api/v1/orders" -Headers $createHeaders -ContentType "application/json" -Body $checkoutBody

Write-Host ("CREATED orderNumber={0} status={1} total={2}" -f $created.data.orderNumber, $created.data.status, $created.data.total) -ForegroundColor Green
Write-Host ("CREATED payload: {0}" -f ($created.data | ConvertTo-Json -Depth 6 -Compress))

$result = @{
    scenario        = "order-number-000006-after-counter-correction"
    idempotencyKey  = $idempotencyKey
    expectedNumber  = "ORD/DB0001/26-27/000006"
    actualNumber    = $created.data.orderNumber
    status          = $created.data.status
    branchCode      = $created.data.branchCode
    orderId         = $created.data.publicId
} | ConvertTo-Json -Depth 8
$result | Out-File -FilePath "scripts/verify-order-000006-result.json" -Encoding utf8
Write-Host "Saved scripts/verify-order-000006-result.json"
