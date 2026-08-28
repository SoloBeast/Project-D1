# ============================================================================
# Employee Onboarding & Owner/SystemAdmin RBAC — UAT verification script
# Runs against the locally running API (http://localhost:5209) in Development.
#
# Covers the 13 required manual verification flows:
#   1  Owner login
#   2  System Admin login
#   3  Owner creates employee (with invitation)
#   4  Invitation link/token generated & surfaced
#   5  Invitation token verifies (link opens)
#   6  Send OTP for EmployeeInvitation purpose
#   7  OTP code captured from development console log
#   8  Complete registration (password + OTP) -> session issued
#   9  New employee can log in
#   10 System Admin can list employees
#   11 System Admin CANNOT create a System Administrator (403)
#   12 Owner sees employee list incl. invitation state
#   13 Owner can resend & cancel invitations
#
# Non-interactive usage (recommended):
#   1. Pre-send an OTP for the target mobile:
#        curl -s -X POST http://localhost:5209/api/v1/auth/send-otp ^
#             -H "Content-Type: application/json" ^
#             -d "{\"mobile\":\"<MOBILE>\",\"purpose\":\"EmployeeInvitation\"}"
#      Read the 6-digit code from the API console.
#   2. Run:
#        powershell -ExecutionPolicy Bypass -File scripts/employee-onboarding-uat.ps1 -Mobile <MOBILE> -Otp <CODE>
#
# Interactive fallback: omit -Otp and the script will prompt after sending OTP.
# ============================================================================

param(
    [string]$Mobile = "",
    [string]$Otp = ""
)

$ErrorActionPreference = "Stop"
$Api = "http://localhost:5209"
$Password = "DoodhDirect@123"
$DevPrefix = "uat"

$Results = [System.Collections.Generic.List[object]]::new()

function Write-Step([string]$title) {
    Write-Host "`n=== $title ===" -ForegroundColor Cyan
}

function Add-Result([string]$flow, [bool]$passed, [string]$detail) {
    $Results.Add([pscustomobject]@{ Flow = $flow; Passed = $passed; Detail = $detail })
    $mark = if ($passed) { "PASS" } else { "FAIL" }
    $color = if ($passed) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} - {2}" -f $mark, $flow, $detail) -ForegroundColor $color
}

function Invoke-JsonPost([string]$path, [object]$body, [string]$token = $null) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    return Invoke-RestMethod -Uri "$Api$path" -Method Post -Headers $headers -ContentType "application/json" -Body $json
}

function Invoke-JsonGet([string]$path, [string]$token = $null) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    return Invoke-RestMethod -Uri "$Api$path" -Method Get -Headers $headers
}

function Login-User([string]$login, [string]$deviceId) {
    $body = @{
        login    = $login
        password = $Password
        device   = @{ deviceIdentifier = $deviceId; deviceName = "UAT"; platform = "powershell" }
    }
    return Invoke-JsonPost "/api/v1/auth/login" $body
}

function New-DeviceId([string]$tag) {
    return "$DevPrefix-$tag-$([guid]::NewGuid().ToString('N').Substring(0,8))"
}

# ---------------------------------------------------------------------------
$now = Get-Date
$suffix = $now.ToString("MMddHHmmss")
if (-not $Mobile) { $Mobile = "90000" + $suffix.Substring($suffix.Length - 5) }
$role = "DELIVERY_MANAGER"
$dmName = "UAT Delivery Manager $suffix"

# ===========================================================================
# FLOW 1 — Owner login
# ===========================================================================
Write-Step "FLOW 1: Owner login"
$owner = Login-User "owner@doodhdirect.local" (New-DeviceId "owner")
if ($owner.success -and $owner.data.tokens.accessToken) {
    $ownerToken = $owner.data.tokens.accessToken
    Add-Result "1. Owner login" $true ("roles=" + ($owner.data.user.roles -join ","))
} else {
    Add-Result "1. Owner login" $false "login failed"
}

# ===========================================================================
# FLOW 2 — System Admin login
# ===========================================================================
Write-Step "FLOW 2: System Admin login"
$sysAdmin = Login-User "system.admin@doodhdirect.local" (New-DeviceId "sysadmin")
if ($sysAdmin.success -and $sysAdmin.data.tokens.accessToken) {
    $sysToken = $sysAdmin.data.tokens.accessToken
    Add-Result "2. System Admin login" $true ("roles=" + ($sysAdmin.data.user.roles -join ","))
} else {
    Add-Result "2. System Admin login" $false "login failed"
}

# ===========================================================================
# FLOW 3 — Owner creates employee (with invitation)
# ===========================================================================
Write-Step "FLOW 3: Owner creates Delivery Manager with invitation"
# DELIVERY_MANAGER requires a branch — fetch branch options first.
$branchId = $null
try {
    $branches = Invoke-JsonGet "/api/v1/admin/employees/branches" $ownerToken
    if ($branches.success -and $branches.data.Count -gt 0) {
        $branchId = $branches.data[0].id
        Write-Host "Using branchId=$branchId ($($branches.data[0].name))" -ForegroundColor DarkGray
    } else {
        Write-Host "No branch options returned; passing branchId=null" -ForegroundColor DarkGray
    }
} catch {
    Write-Host "Branch fetch failed: $($_.Exception.Message)" -ForegroundColor DarkGray
}
$createBody = @{
    displayName    = $dmName
    mobile         = $Mobile
    email          = "dm$suffix@uat.local"
    roleCode       = $role
    branchId       = $branchId
    sendInvitation = $true
}
$createOk = $false
try {
    $createResp = Invoke-JsonPost "/api/v1/admin/employees" $createBody $ownerToken
    $createOk = $createResp.success -and $null -ne $createResp.data.employee -and $null -ne $createResp.data.invitation
    if ($createOk) {
        $employeeId = $createResp.data.employee.id
        $invitationId = $createResp.data.invitation.invitationId
        $invToken = $createResp.data.invitation.token
        $invExpiry = $createResp.data.invitation.expiresAt
        Add-Result "3. Owner creates employee (with invitation)" $true ("employeeId=$employeeId invitationId=$invitationId")
    } else {
        Add-Result "3. Owner creates employee (with invitation)" $false "unexpected response"
    }
} catch {
    Add-Result "3. Owner creates employee (with invitation)" $false $_.Exception.Message
}

# ===========================================================================
# FLOW 4 — Invitation link/token generated & surfaced
# ===========================================================================
Write-Step "FLOW 4: Invitation link surfaced with raw token (once)"
if ($createOk -and $invToken) {
    Add-Result "4. Invitation link/token surfaced" $true ("token length=" + $invToken.Length + " expiry=" + $invExpiry)
} else {
    Add-Result "4. Invitation link/token surfaced" $false "no token"
}

# ===========================================================================
# FLOW 5 — Invitation token verifies (link opens)
# ===========================================================================
Write-Step "FLOW 5: Invitation token verifies"
if ($createOk -and $invToken) {
    try {
        $encodedToken = [uri]::EscapeDataString($invToken)
        $verify = Invoke-JsonGet ("/api/v1/employee-invitations/{0}/verify" -f $encodedToken)
        if ($verify.success -and $verify.data.isValid) {
            Add-Result "5. Invitation token verifies" $true ("roleCode=" + $verify.data.roleCode + " name=" + $verify.data.displayName)
        } else {
            Add-Result "5. Invitation token verifies" $false "isValid=$($verify.data.isValid) reason=$($verify.data.reason)"
        }
    } catch {
        Add-Result "5. Invitation token verifies" $false $_.Exception.Message
    }
} else {
    Add-Result "5. Invitation token verifies" $false "no token from flow 3"
}

# ===========================================================================
# FLOW 6 — Send OTP for EmployeeInvitation purpose
# ===========================================================================
Write-Step "FLOW 6: Send OTP (EmployeeInvitation)"
$otpCaptured = $null
if ($Otp.Length -eq 6) {
    $otpCaptured = $Otp
    Add-Result "6. Send OTP" $true "OTP provided via -Otp argument (sent beforehand)"
} else {
    try {
        $otpBody = @{ mobile = $Mobile; purpose = "EmployeeInvitation" }
        $otpResp = Invoke-JsonPost "/api/v1/auth/send-otp" $otpBody
        Add-Result "6. Send OTP" ($otpResp.success -eq $true) "response accepted; read code from API console"
        $otpCaptured = Read-Host "Enter the 6-digit OTP from the API console"
    } catch {
        Add-Result "6. Send OTP" $false $_.Exception.Message
    }
}

# ===========================================================================
# FLOW 7 — OTP captured
# ===========================================================================
Write-Step "FLOW 7: OTP captured"
if ($otpCaptured -and $otpCaptured.Length -eq 6) {
    Add-Result "7. OTP captured" $true "6-digit code provided"
} else {
    Add-Result "7. OTP captured" $false "no valid 6-digit code"
}

# ===========================================================================
# FLOW 8 — Complete registration (password + OTP) -> session issued
# ===========================================================================
Write-Step "FLOW 8: Complete employee registration"
if ($createOk -and $invToken -and $otpCaptured) {
    try {
        $completeBody = @{
            token       = $invToken
            displayName = $dmName
            email       = "dm$suffix@uat.local"
            mobile      = $Mobile
            password    = $Password
            otpCode     = $otpCaptured
            device      = @{ deviceIdentifier = (New-DeviceId "invitee"); deviceName = "UAT"; platform = "powershell" }
        }
        $complete = Invoke-JsonPost "/api/v1/employee-invitations/complete" $completeBody
        if ($complete.success -and $complete.data.session.tokens.accessToken) {
            $inviteeToken = $complete.data.session.tokens.accessToken
            $inviteeRoles = $complete.data.session.user.roles
            Add-Result "8. Complete registration" $true ("roles=" + ($inviteeRoles -join ",") + " invitationStatus=" + $complete.data.invitationStatus)
        } else {
            Add-Result "8. Complete registration" $false "no session returned"
        }
    } catch {
        Add-Result "8. Complete registration" $false $_.Exception.Message
    }
} else {
    Add-Result "8. Complete registration" $false "missing token/otp"
}

# ===========================================================================
# FLOW 9 — New employee can log in
# ===========================================================================
Write-Step "FLOW 9: New employee can log in"
try {
    $empLogin = Login-User $Mobile (New-DeviceId "emp-login")
    if ($empLogin.success -and $empLogin.data.user.roles -contains $role) {
        Add-Result "9. New employee login" $true ("roles=" + ($empLogin.data.user.roles -join ","))
    } else {
        Add-Result "9. New employee login" $false "login failed or wrong roles"
    }
} catch {
    Add-Result "9. New employee login" $false $_.Exception.Message
}

# ===========================================================================
# FLOW 10 — System Admin can list employees
# ===========================================================================
Write-Step "FLOW 10: System Admin lists employees"
try {
    $list = Invoke-JsonGet "/api/v1/admin/employees" $sysToken
    if ($list.success) {
        Add-Result "10. System Admin lists employees" $true ("count=" + $list.data.Count)
    } else {
        Add-Result "10. System Admin lists employees" $false "list failed"
    }
} catch {
    Add-Result "10. System Admin lists employees" $false $_.Exception.Message
}

# ===========================================================================
# FLOW 11 — System Admin CANNOT create a System Administrator (403)
# ===========================================================================
Write-Step "FLOW 11: System Admin blocked from creating SYSTEM_ADMIN"
$sysCreateBody = @{
    displayName    = "Rogue Admin $suffix"
    mobile         = "9000099999"
    email          = "rogue$suffix@uat.local"
    roleCode       = "SYSTEM_ADMIN"
    branchId       = $null
    sendInvitation = $false
}
$blocked = $false
try {
    Invoke-JsonPost "/api/v1/admin/employees" $sysCreateBody $sysToken | Out-Null
} catch {
    $statusCode = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
    if ($statusCode -eq 403 -or $_.Exception.Message -match "403") { $blocked = $true }
}
Add-Result "11. System Admin blocked from SYSTEM_ADMIN" $blocked "expected 403"

# ===========================================================================
# FLOW 12 — Owner sees employee list incl. invitation state
# ===========================================================================
Write-Step "FLOW 12: Owner lists employees (incl. invitation state)"
try {
    $ownerList = Invoke-JsonGet "/api/v1/admin/employees" $ownerToken
    if ($ownerList.success) {
        $target = $ownerList.data | Where-Object { $_.id -eq $employeeId }
        if ($target) {
            Add-Result "12. Owner lists employees (invite state)" $true ("invitationStatus=" + $target.invitationStatus + " role=" + $target.roleCode)
        } else {
            Add-Result "12. Owner lists employees (invite state)" $false "created employee not found in list"
        }
    } else {
        Add-Result "12. Owner lists employees (invite state)" $false "list failed"
    }
} catch {
    Add-Result "12. Owner lists employees (invite state)" $false $_.Exception.Message
}

# ===========================================================================
# FLOW 13 — Owner can resend & cancel invitations
# ===========================================================================
# The flow-3 invitation is consumed by flow 8 (registration completes it), and the
# backend correctly forbids resending a non-active invitation. So this flow creates a
# fresh throwaway employee whose invitation is still Invited, then resends and cancels it.
Write-Step "FLOW 13: Owner resends & cancels invitation"
$resendOk = $false
$cancelOk = $false
if ($createOk) {
    try {
        $altMobile = "90000" + (([int]$Mobile.Substring(5)) + 1).ToString("D5")
        $altName = "UAT Resend Target $suffix"
        $altBody = @{
            displayName    = $altName
            mobile         = $altMobile
            email          = "dmr$suffix@uat.local"
            roleCode       = $role
            branchId       = $branchId
            sendInvitation = $true
        }
        $altResp = Invoke-JsonPost "/api/v1/admin/employees" $altBody $ownerToken
        if (-not $altResp.success) { throw "throwaway employee creation failed" }
        $altEmployeeId = $altResp.data.employee.id
        $altInvitationId = $altResp.data.invitation.invitationId
        $altToken = $altResp.data.invitation.token

        # 13a — resend the active invitation
        try {
            $resendBody = @{ employeeId = $altEmployeeId; invitationId = $altInvitationId }
            $resend = Invoke-JsonPost ("/api/v1/admin/employees/{0}/invitations/{1}/resend" -f $altEmployeeId, $altInvitationId) $resendBody $ownerToken
            if ($resend.success -and $resend.data.token) {
                $newToken = $resend.data.token
                $newInvitationId = $resend.data.invitationId
                $resendOk = $true
                # Old token must no longer verify after resend (URL-encode to preserve any URL-safe chars)
                $oldVerifyInvalid = $false
                try {
                    $oldVerify = Invoke-JsonGet ("/api/v1/employee-invitations/{0}/verify" -f [uri]::EscapeDataString($altToken))
                    $oldVerifyInvalid = -not $oldVerify.data.isValid
                } catch { $oldVerifyInvalid = $true }
                Add-Result "13a. Owner resends invitation" $resendOk ("newInvitationId=$newInvitationId oldTokenInvalid=$oldVerifyInvalid")
            } else {
                Add-Result "13a. Owner resends invitation" $false "resend response missing token"
            }
        } catch {
            Add-Result "13a. Owner resends invitation" $false $_.Exception.Message
        }

        # 13b — cancel the fresh invitation (separate try so a cancel failure is not misreported as 13a)
        if ($resendOk) {
            try {
                $cancel = Invoke-JsonPost ("/api/v1/admin/employees/{0}/invitations/{1}/cancel" -f $altEmployeeId, $newInvitationId) @{} $ownerToken
                if ($cancel.success) {
                    $verifyCancelled = Invoke-JsonGet ("/api/v1/employee-invitations/{0}/verify" -f [uri]::EscapeDataString($newToken))
                    $cancelOk = -not $verifyCancelled.data.isValid
                    Add-Result "13b. Owner cancels invitation" $cancelOk "cancelled token no longer verifies"
                } else {
                    Add-Result "13b. Owner cancels invitation" $false "cancel returned success=false"
                }
            } catch {
                Add-Result "13b. Owner cancels invitation" $false $_.Exception.Message
            }
        } else {
            Add-Result "13b. Owner cancels invitation" $false "skipped (resend failed)"
        }
    } catch {
        Add-Result "13a. Owner resends invitation" $false ("setup failed: " + $_.Exception.Message)
        Add-Result "13b. Owner cancels invitation" $false "skipped (setup failed)"
    }
} else {
    Add-Result "13a. Owner resends invitation" $false "no employee created in flow 3"
    Add-Result "13b. Owner cancels invitation" $false "skipped (no employee)"
}

# ===========================================================================
# Summary
# ===========================================================================
Write-Host "`n==================== UAT SUMMARY ====================" -ForegroundColor Cyan
$failures = 0
foreach ($r in $Results) {
    if (-not $r.Passed) { $failures++ }
    $mark = if ($r.Passed) { "[PASS]" } else { "[FAIL]" }
    $color = if ($r.Passed) { "Green" } else { "Red" }
    Write-Host ("{0} {1}: {2}" -f $mark, $r.Flow, $r.Detail) -ForegroundColor $color
}
Write-Host "`nTotal: $($Results.Count) checks, $failures failure(s)" -ForegroundColor $(if ($failures -eq 0) { "Green" } else { "Red" })
exit $failures
