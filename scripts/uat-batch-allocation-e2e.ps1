# Live UAT E2E: batch-allocation flow (todo #7 of the DeliveryBatchAllocationId incident).
# Story: Hemant (DM, branch 6) -> sees available batches on delivery 53 -> saves multi-batch
#        allocation (1.5 + 1.5 = 3 L) -> assigns Jaidev -> Jaidev pickup/start/arrive ->
#        verify-otp (terminal completing action) -> automatic consumption from persisted
#        allocation -> availability decreases exactly once.
#
# Prereqs (already satisfied):
#   * Migration 20260909065742_DeliveryBatchAllocations applied (column, table, FK, indexes verified).
#   * API running in Development on http://localhost:5209.
#   * OTP id 136 (delivery 53) unconsumed; known pbkdf2 CodeHash injected by this script.
$ErrorActionPreference = "Stop"
$base = "http://localhost:5209"

# ---------------------------------------------------------------------------
# Config (verified against live DB)
# ---------------------------------------------------------------------------
$dmLogin    = "919354816929"   # Hemant Sharma (user 36) DAIRY_MANAGER branch 6 (ASSIGN_BRANCH + DAIRY.MANAGE)
$dmPassword = "DoodhDirect@123"
$staffLogin = "919654487413"   # Jaidev (user 38) DELIVERY_STAFF branch 6
$deliveryPublicId = "51C475BA-340E-4825-BA67-45B9BD31864C"   # delivery 53
$staffPublicId    = "6DE7E692-AFB5-40B7-8759-1F532397E867"   # Jaidev
$branchId   = 6
$knownOtp   = "246810"        # injected CodeHash; must match verify-otp code
$requiredQty = 3.0            # delivery 53 requires 3.0 L (order 67: Buffalo Milk x 3.0 L)

$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$title) { Write-Host "`n==== $title ====" -ForegroundColor Cyan }
function Write-Result([string]$label, [bool]$ok, [string]$detail = "") {
    $marker = if ($ok) { "[PASS]" } else { "[FAIL]" }
    $color  = if ($ok) { "Green" } else { "Red" }
    if (-not $ok) { $script:failures.Add("$label :: $detail") }
    Write-Host ("  {0} {1} {2}" -f $marker, $label, $detail) -ForegroundColor $color
}

# ---------------------------------------------------------------------------
# HTTP helpers (mirror scripts/uat-availability-check.ps1)
# ---------------------------------------------------------------------------
function Invoke-Login {
    param([string]$Login, [string]$Password)
    $body = @{
        login    = $Login
        password = $Password
        device   = @{
            deviceIdentifier = "uat-batch-e2e"
            deviceName       = "PowerShell UAT E2E"
            platform         = "powershell"
        }
    } | ConvertTo-Json -Depth 5
    $resp = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType "application/json" -Body $body
    if ($null -eq $resp.data.tokens.accessToken) { throw "Login succeeded but no accessToken in response" }
    return $resp.data
}

function Invoke-Api {
    param([string]$Token, [string]$Method, [string]$Path, $Body = $null)
    $headers = @{ Authorization = "Bearer $Token" }
    $params  = @{ Method = $Method; Uri = "$base$Path"; Headers = $headers }
    if ($null -ne $Body) {
        $params.ContentType = "application/json"
        $params.Body        = ($Body | ConvertTo-Json -Depth 10)
    }
    try {
        $resp = Invoke-RestMethod @params
        return [pscustomobject]@{ Http = 200; Success = $true; Data = $resp }
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        return [pscustomobject]@{ Http = $status; Success = $false; Data = $_.ErrorDetails.Message }
    }
}

function Invoke-Availability([string]$Token) {
    $r = Invoke-Api -Token $Token -Method Get -Path "/api/v1/dairy/branches/$branchId/availability"
    if ($r.Success) {
        return [pscustomobject]@{
            Http       = 200
            Produced   = [decimal]$r.Data.data.quantityProduced
            Used       = [decimal]$r.Data.data.quantityUsed
            Available  = [decimal]$r.Data.data.availableQuantity
            BatchCount = [int]$r.Data.data.availableBatchCount
        }
    }
    return [pscustomobject]@{ Http = $r.Http; Produced = -1; Used = -1; Available = -1; BatchCount = -1 }
}

# ---------------------------------------------------------------------------
# 0. Defensive reset (idempotent): delivery 53 -> ReadyForAssignment, order 67 -> Confirmed,
#    purge any leftover allocations/usage rows for delivery 53.
#    QUOTED_IDENTIFIER required by Delivery table.
# ---------------------------------------------------------------------------
Write-Step "0. Defensive reset of delivery 53 / order 67"
$resetSql = "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
  DELETE FROM dbo.MilkUsage WHERE DeliveryId=53;
  DELETE FROM dbo.DeliveryBatchAllocation WHERE DeliveryId=53;
  UPDATE dbo.Delivery SET Status='ReadyForAssignment', AssignedEmployeeId=NULL, AssignedAtUtc=NULL,
         PickedUpAtUtc=NULL, OutForDeliveryAtUtc=NULL, ArrivedAtUtc=NULL, OtpVerifiedAtUtc=NULL,
         CompletedAtUtc=NULL, FailedAtUtc=NULL, FailureReason=NULL, FailureLatitude=NULL, FailureLongitude=NULL
   WHERE Id=53;
  UPDATE dbo.[Order] SET Status='Confirmed' WHERE Id=67;
  UPDATE dbo.DeliveryOtp SET AttemptCount=0, ConsumedAtUtc=NULL WHERE Id=136;"
$resetOut = sqlcmd -S .\SQLEXPRESS -E -C -d DoodhDirect -I -b -h -1 -Q $resetSql 2>$null
Write-Result "Reset delivery/order/allocations/usage (idempotent)" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

# ---------------------------------------------------------------------------
# 1. Logins (DM + staff)
# ---------------------------------------------------------------------------
Write-Step "1. Logins (DM + staff)"
$dm = Invoke-Login -Login $dmLogin -Password $dmPassword
$dmToken = $dm.tokens.accessToken
Write-Result "DM login (Hemant, branch $branchId)" ($null -ne $dmToken) "roles=$($dm.user.roles -join ',')"

$staff = Invoke-Login -Login $staffLogin -Password $dmPassword
$staffToken = $staff.tokens.accessToken
Write-Result "Staff login (Jaidev, branch $branchId)" ($null -ne $staffToken) "roles=$($staff.user.roles -join ',')"

# ---------------------------------------------------------------------------
# 2. Baseline availability (before any new production / consumption)
# ---------------------------------------------------------------------------
Write-Step "2. Baseline availability (branch $branchId)"
$avBase = Invoke-Availability $dmToken
Write-Result "availability baseline 200" ($avBase.Http -eq 200) "produced=$($avBase.Produced) used=$($avBase.Used) available=$($avBase.Available) batches=$($avBase.BatchCount)"

# ---------------------------------------------------------------------------
# 3. Record a second production batch (5 L) so the allocation screen shows 2 eligible batches
# ---------------------------------------------------------------------------
Write-Step "3. Record second production batch (branch $branchId, 5 L)"
$now = Get-Date
$prodAt = [DateTime]::new($now.Year, $now.Month, $now.Day, $now.Hour, $now.Minute, 0)
$prod = Invoke-Api -Token $dmToken -Method Post -Path "/api/v1/dairy/branches/$branchId/production" -Body @{
    productionAt     = $prodAt.ToString("yyyy-MM-ddTHH:mm:ss")
    shift            = "UAT-E2E"
    buffaloCount     = 2
    quantityProduced = 5.0
    unit             = "L"
    remarks          = "UAT E2E batch-allocation verification"
}
$prodBatchPublicId = $null; $prodBatchNumber = $null
if ($prod.Success -and $prod.Data.data.batch) {
    $prodBatchPublicId = $prod.Data.data.batch.publicId
    $prodBatchNumber   = $prod.Data.data.batch.batchNumber
}
Write-Result "Record production -> new batch $prodBatchNumber (publicId=$prodBatchPublicId)" ($prod.Success -and $null -ne $prodBatchNumber) "HTTP $($prod.Http) $($prod.Data)"

$avAfterProd = Invoke-Availability $dmToken
Write-Result "Availability after production (produced +5, used unchanged)" ($avAfterProd.Produced -eq ($avBase.Produced + 5) -and $avAfterProd.Used -eq $avBase.Used) "produced=$($avAfterProd.Produced) used=$($avAfterProd.Used) available=$($avAfterProd.Available)"

# ---------------------------------------------------------------------------
# 4. Batch-allocations screen: eligible batches load + no allocations yet.
#    Numeric batch ids come from eligibleBatches[].batchId (NOT the production response).
# ---------------------------------------------------------------------------
Write-Step "4. GET batch-allocations (eligible batches + current allocations)"
$screen = Invoke-Api -Token $dmToken -Method Get -Path "/api/v1/delivery-management/$deliveryPublicId/batch-allocations"
$elig = @(); $aloc = @(); $tot = $null
if ($screen.Success) {
    $elig = $screen.Data.data.eligibleBatches
    $aloc = $screen.Data.data.allocations
    $tot  = $screen.Data.data.totalRequiredQuantity
    Write-Result "Batch-allocations GET 200" $true "eligible=$($elig.Count) allocations=$($aloc.Count) totalRequired=$tot"
    $elig | ForEach-Object { Write-Host ("      batch {0} '{1}' produced={2} available={3} status={4}" -f $_.batchId, $_.batchNumber, $_.quantityProduced, $_.quantityAvailable, $_.status) }
    Write-Result "Eligible batches >= 2 (multi-batch screen)" ($elig.Count -ge 2) "count=$($elig.Count)"
    Write-Result "TotalRequiredQuantity = $requiredQty" ([decimal]$tot -eq $requiredQty) "tot=$tot"
    Write-Result "No allocations yet (fresh)" ($aloc.Count -eq 0) "count=$($aloc.Count)"
}
else {
    Write-Result "Batch-allocations GET 200" $false "HTTP $($screen.Http) $($screen.Data)"
}

# ---------------------------------------------------------------------------
# 5. Multi-batch allocation: 1.5 L from batch 3 + 1.5 L from the new batch = 3 L
#    Derive numeric ids from the eligibleBatches payload.
# ---------------------------------------------------------------------------
Write-Step "5. PUT batch-allocations (multi-batch: 1.5 + 1.5 = 3)"
$idBatch3 = [long]3
$idBatch4 = $null
$newBatchFound = $false
if ($screen.Success -and $elig.Count -gt 0) {
    $match = $elig | Where-Object { $_.batchNumber -eq $prodBatchNumber } | Select-Object -First 1
    if ($match) { $idBatch4 = [long]$match.batchId; $newBatchFound = $true }
}
if (-not $newBatchFound) {
    $other = $elig | Where-Object { [long]$_.batchId -ne 3 } | Select-Object -First 1
    if ($other) { $idBatch4 = [long]$other.batchId; $newBatchFound = $true }
}
Write-Result "Resolved numeric ids: batch3=$idBatch3 batch4=$idBatch4" ($newBatchFound -and $null -ne $idBatch4) "matched=$newBatchFound"

$save = $null
if ($newBatchFound -and $null -ne $idBatch4) {
    $save = Invoke-Api -Token $dmToken -Method Put -Path "/api/v1/delivery-management/$deliveryPublicId/batch-allocations" -Body @{
        allocations = @(
            @{ batchId = $idBatch3; quantityAllocated = 1.5 },
            @{ batchId = $idBatch4; quantityAllocated = 1.5 }
        )
    }
}
if ($save -and $save.Success) {
    $saved = $save.Data.data.batchAllocations
    Write-Result "PUT batch-allocations 200" $true "saved=$($saved.Count)"
    $saved | ForEach-Object { Write-Host ("      saved batch {0} '{1}' qty={2}" -f $_.batchId, $_.batchNumber, $_.quantityAllocated) }
    Write-Result "Two allocations persisted" ($saved.Count -eq 2) "count=$($saved.Count)"
    $sum = ($saved | Measure-Object -Property quantityAllocated -Sum).Sum
    Write-Result "Allocation total = $requiredQty" ([decimal]$sum -eq $requiredQty) "sum=$sum"
}
elseif ($save) {
    Write-Result "PUT batch-allocations 200" $false "HTTP $($save.Http) $($save.Data)"
}
else {
    Write-Result "PUT batch-allocations 200" $false "no-op (batch id resolution failed)"
}

# ---------------------------------------------------------------------------
# 6. Re-read screen: saved allocation returned (idempotent persistence)
# ---------------------------------------------------------------------------
Write-Step "6. GET batch-allocations again (saved returned)"
$screen2 = Invoke-Api -Token $dmToken -Method Get -Path "/api/v1/delivery-management/$deliveryPublicId/batch-allocations"
if ($screen2.Success) {
    $aloc2 = $screen2.Data.data.allocations
    Write-Result "Saved allocations returned" ($aloc2.Count -eq 2) "count=$($aloc2.Count)"
    $aloc2 | ForEach-Object { Write-Host ("      persisted batch {0} '{1}' qty={2}" -f $_.batchId, $_.batchNumber, $_.quantityAllocated) }
}
else {
    Write-Result "Saved allocations returned" $false "HTTP $($screen2.Http) $($screen2.Data)"
}

# ---------------------------------------------------------------------------
# 7. Assign Jaidev (delivery is ReadyForAssignment after the reset)
# ---------------------------------------------------------------------------
Write-Step "7. Assign delivery 53 to Jaidev"
$assign = Invoke-Api -Token $dmToken -Method Post -Path "/api/v1/delivery-management/$deliveryPublicId/assign" -Body @{
    employeeId = $staffPublicId
    reason     = "UAT E2E batch allocation"
}
if ($assign.Success) {
    $asn = $assign.Data.data.assignments | Select-Object -First 1
    Write-Result "Assign 200" $true "employee=$($asn.employeeId) assignedAt=$($asn.assignedAt)"
}
else {
    Write-Result "Assign 200" $false "HTTP $($assign.Http) $($assign.Data)"
}

# ---------------------------------------------------------------------------
# 8. Inject known OTP hash (id 136) so verify-otp can pass.
#    CodeHash must be pbkdf2-sha512-v1$120000$salt$key for code '246810'.
# ---------------------------------------------------------------------------
Write-Step "8. Inject OTP CodeHash for known code '$knownOtp' (otp id 136)"
$hash = $null
$exeCandidates = @(
    "$PSScriptRoot\tools\pbkdf2-gen\bin\Debug\net10.0\pbkdf2-gen.exe",
    "$PSScriptRoot\tools\pbkdf2-gen\pbkdf2-gen.exe"
)
foreach ($tool in $exeCandidates) {
    if (Test-Path $tool) {
        try { $hash = (& $tool $knownOtp 2>$null | Select-Object -Last 1) } catch { $hash = $null }
        if ($hash -like "pbkdf2-sha512-v1*") { break }
    }
}
if (-not $hash -or $hash -notlike "pbkdf2-sha512-v1*") {
    try {
        $hash = (& dotnet run --project "$PSScriptRoot\tools\pbkdf2-gen" -- $knownOtp 2>$null | Select-Object -Last 1)
    } catch { $hash = $null }
}
if (-not $hash -or $hash -notlike "pbkdf2-sha512-v1*") {
    Write-Result "Generate OTP hash" $false "could not produce pbkdf2 hash"
}
else {
    $esc = $hash.Replace("'", "''")
    $sql = "SET QUOTED_IDENTIFIER ON; SET NOCOUNT ON;
      UPDATE dbo.DeliveryOtp SET CodeHash = '$esc', AttemptCount = 0, ConsumedAtUtc = NULL,
             SentAtUtc = GETUTCDATE(), ExpiresAtUtc = DATEADD(MINUTE, 30, GETUTCDATE())
       WHERE Id = 136 AND DeliveryId = 53;
      SELECT @@ROWCOUNT;"
    $rows = @(sqlcmd -S .\SQLEXPRESS -E -C -d DoodhDirect -I -b -h -1 -Q $sql 2>$null)
    $rowsAffected = if ($rows.Count -gt 0) { $rows[0].Trim() } else { "" }
    Write-Result "Inject OTP hash (code '$knownOtp', otp id 136)" ($rowsAffected -eq "1") "rows=$rowsAffected"
}

# ---------------------------------------------------------------------------
# 9. Staff journey: pickup -> start -> arrive (verify-otp needs status Arrived)
# ---------------------------------------------------------------------------
Write-Step "9. Staff journey: pickup / start / arrive"
$pickup = Invoke-Api -Token $staffToken -Method Post -Path "/api/v1/delivery/$deliveryPublicId/pickup" -Body @{ remarks = "Picked up in UAT E2E" }
if ($pickup.Success) {
    Write-Result "Pickup 200 -> $($pickup.Data.data.status)" ($pickup.Data.data.status -eq "PickedUp") "status=$($pickup.Data.data.status)"
} else {
    Write-Result "Pickup 200" $false "HTTP $($pickup.Http) $($pickup.Data)"
}

$start = Invoke-Api -Token $staffToken -Method Post -Path "/api/v1/delivery/$deliveryPublicId/start"
if ($start.Success) {
    Write-Result "Start 200 -> OutForDelivery" ($start.Data.data.status -eq "OutForDelivery") "status=$($start.Data.data.status)"
} else {
    Write-Result "Start 200" $false "HTTP $($start.Http) $($start.Data)"
}

$arrive = Invoke-Api -Token $staffToken -Method Post -Path "/api/v1/delivery/$deliveryPublicId/arrive"
if ($arrive.Success) {
    Write-Result "Arrive 200 -> Arrived" ($arrive.Data.data.status -eq "Arrived") "status=$($arrive.Data.data.status)"
} else {
    Write-Result "Arrive 200" $false "HTTP $($arrive.Http) $($arrive.Data)"
}

# ---------------------------------------------------------------------------
# 10. verify-otp with known code -> terminal Delivered transition. Consumption
#     is created here (VerifyOtpAsync -> CompleteDeliveryMutationAsync).
# ---------------------------------------------------------------------------
Write-Step "10. Verify OTP '$knownOtp' -> Delivered (creates consumption)"
$verify = Invoke-Api -Token $staffToken -Method Post -Path "/api/v1/delivery/$deliveryPublicId/verify-otp" -Body @{ code = $knownOtp }
if ($verify.Success) {
    $v = $verify.Data.data
    Write-Result "Verify-otp 200 -> Delivered" ($v.status -eq "Delivered") "status=$($v.status)"
    Write-Result "CompletedAt populated" ($null -ne $v.completedAt) "completedAt=$($v.completedAt)"
    Write-Result "OtpVerifiedAt populated" ($null -ne $v.otpVerifiedAt) "otpVerifiedAt=$($v.otpVerifiedAt)"
    Write-Result "Two batchAllocations on result" ($v.batchAllocations.Count -eq 2) "count=$($v.batchAllocations.Count)"
} else {
    Write-Result "Verify-otp 200 -> Delivered" $false "HTTP $($verify.Http) $($verify.Data)"
}

# ---------------------------------------------------------------------------
# 11. Management GET after completion (status, CompletedAt, persisted allocations)
# ---------------------------------------------------------------------------
Write-Step "11. Management GET after completion"
$mgmt = Invoke-Api -Token $dmToken -Method Get -Path "/api/v1/delivery-management/$deliveryPublicId"
if ($mgmt.Success) {
    $m = $mgmt.Data.data
    Write-Result "Management GET 200 -> Delivered" ($m.status -eq "Delivered") "status=$($m.status)"
    Write-Result "CompletedAt set on delivery" ($null -ne $m.completedAt) "completedAt=$($m.completedAt)"
    Write-Result "Two batchAllocations persisted" ($m.batchAllocations.Count -eq 2) "count=$($m.batchAllocations.Count)"
} else {
    Write-Result "Management GET 200" $false "HTTP $($mgmt.Http) $($mgmt.Data)"
}

# ---------------------------------------------------------------------------
# 12. Availability decreased exactly once by the full allocated quantity (3 L)
#     relative to the post-production baseline.
# ---------------------------------------------------------------------------
Write-Step "12. Availability after delivery (delta = -3)"
$avAfter = Invoke-Availability $dmToken
if ($avAfter.Http -eq 200) {
    $usedDelta = [decimal]$avAfter.Used - [decimal]$avAfterProd.Used
    $availDelta = [decimal]$avAfter.Available - [decimal]$avAfterProd.Available
    Write-Result "Availability after delivery 200" $true "produced=$($avAfter.Produced) used=$($avAfter.Used) available=$($avAfter.Available)"
    Write-Result "Used increased by exactly $requiredQty" ($usedDelta -eq $requiredQty) "usedDelta=$usedDelta"
    Write-Result "Available decreased by exactly $requiredQty" ($availDelta -eq (-$requiredQty)) "availDelta=$availDelta"
    Write-Result "Produced unchanged after completion" ($avAfter.Produced -eq $avAfterProd.Produced) "produced=$($avAfter.Produced)"
} else {
    Write-Result "Availability after delivery 200" $false "HTTP $($avAfter.Http)"
}

# ---------------------------------------------------------------------------
# 13. SQL: MilkUsage rows created from persisted allocations (2 rows, sum 3 L,
#     each linked via DeliveryBatchAllocationId) with CompletedAtUtc set.
# ---------------------------------------------------------------------------
Write-Step "13. SQL verification of automatic consumption"
$usageSql = "SET NOCOUNT ON;
  SELECT COUNT(*) FROM dbo.MilkUsage WHERE DeliveryId = 53;
  SELECT ISNULL(SUM(QuantityUsed), 0) FROM dbo.MilkUsage WHERE DeliveryId = 53;
  SELECT COUNT(*) FROM dbo.MilkUsage WHERE DeliveryId = 53 AND DeliveryBatchAllocationId IS NOT NULL;
  SELECT COUNT(*) FROM dbo.Delivery WHERE Id = 53 AND Status = 'Delivered' AND CompletedAtUtc IS NOT NULL;"
$usageOut = @(sqlcmd -S .\SQLEXPRESS -E -C -d DoodhDirect -I -b -h -1 -Q $usageSql 2>$null)
$usageCount     = if ($usageOut.Count -gt 0) { [int]$usageOut[0].Trim() } else { -1 }
$usageSum       = if ($usageOut.Count -gt 1) { [decimal]$usageOut[1].Trim() } else { -1 }
$linkedCount    = if ($usageOut.Count -gt 2) { [int]$usageOut[2].Trim() } else { -1 }
$deliveredCount = if ($usageOut.Count -gt 3) { [int]$usageOut[3].Trim() } else { -1 }
Write-Result "SQL exit 0" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"
Write-Result "MilkUsage rows = 2 (one per allocation)" ($usageCount -eq 2) "rows=$usageCount"
Write-Result "MilkUsage sum = $requiredQty" ($usageSum -eq $requiredQty) "sum=$usageSum"
Write-Result "Both rows linked to DeliveryBatchAllocationId" ($linkedCount -eq 2) "linked=$linkedCount"
Write-Result "Delivery 53 Delivered + CompletedAtUtc set" ($deliveredCount -eq 1) "rows=$deliveredCount"

# ---------------------------------------------------------------------------
# 14. Guard check: allocations cannot be changed after Delivered (EnsureAllocatable
#     returns HTTP 422 BUSINESS_RULE); consumption is not duplicated.
# ---------------------------------------------------------------------------
Write-Step "14. Post-completion guard: PUT batch-allocations rejected (422)"
$resave = Invoke-Api -Token $dmToken -Method Put -Path "/api/v1/delivery-management/$deliveryPublicId/batch-allocations" -Body @{
    allocations = @(
        @{ batchId = $idBatch3; quantityAllocated = 1.5 },
        @{ batchId = $idBatch4; quantityAllocated = 1.5 }
    )
}
Write-Result "Re-save PUT rejected 422 (delivery Delivered)" ($resave.Http -eq 422 -and -not $resave.Success) "HTTP $($resave.Http)"
$usageAfterSql = "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.MilkUsage WHERE DeliveryId = 53;"
$usageAfterOut = @(sqlcmd -S .\SQLEXPRESS -E -C -d DoodhDirect -I -b -h -1 -Q $usageAfterSql 2>$null)
$usageAfter = if ($usageAfterOut.Count -gt 0) { [int]$usageAfterOut[0].Trim() } else { -1 }
Write-Result "Usage count still 2 after rejected re-save (no duplicates)" ($usageAfter -eq 2) "rows=$usageAfter"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Step "SUMMARY"
Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "  E2E batch-allocation verification PASSED" -ForegroundColor Green
    Write-Host "  - eligible batches loaded, multi-batch allocation saved and returned" -ForegroundColor Green
    Write-Host "  - staff journey + OTP verification delivered delivery 53" -ForegroundColor Green
    Write-Host "  - automatic consumption created exactly once from persisted allocations" -ForegroundColor Green
    Write-Host "  - availability decreased exactly $requiredQty L" -ForegroundColor Green
} else {
    Write-Host "  E2E batch-allocation verification FAILED ($($failures.Count) failure(s)):" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
}
Write-Host ""
exit $(if ($failures.Count -eq 0) { 0 } else { 1 })
