# ============================================================================
# reset-development-for-uat.ps1
#
# Resets the DoodhDirect development database (SQL Server Express) to a clean
# UAT baseline. Deletes ALL business/test data in FK-safe order. Reference and
# identity data are intentionally removed as well and re-created by the API's
# seed services on next startup:
#
#   - IdentitySeedService          -> Roles, Permissions, RolePermissions
#   - NumberSeriesSeedService      -> global NumberSeries (CUSTOMER/ORDER/BRANCH/DELIVERY)
#   - CatalogueSeedService         -> MAIN branch, MILK category, FRESH-BUFFALO-MILK
#                                     product + branch availability, scoped ORDER series
#   - NotificationTemplateSeedService -> NotificationTemplate rows
#   - Development*UserSeedServices -> the 8 development/UAT users (Development env only)
#
# SAFETY:
#   * Refuses to run against any database other than 'DoodhDirect'.
#   * Defaults to DRY-RUN: prints the deletion plan with row counts only.
#   * Pass -Confirm to actually execute the deletes (in a single transaction).
#
# USAGE:
#   powershell -ExecutionPolicy Bypass -File scripts/reset-development-for-uat.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/reset-development-for-uat.ps1 -Confirm
#   powershell -ExecutionPolicy Bypass -File scripts/reset-development-for-uat.ps1 -Confirm -Force
#     (-Force skips the interactive 'RESET' prompt for automated runs)
# ============================================================================

param(
    [string]$ServerInstance = ".\SQLEXPRESS",
    [string]$Database = "DoodhDirect",
    [switch]$Confirm,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Safety: never allow anything but the intended UAT/development database.
# ---------------------------------------------------------------------------
if ($Database -ne "DoodhDirect") {
    throw "Refusing to reset '$Database'. This script may only target the 'DoodhDirect' database."
}

$sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
if (-not $sqlcmd) {
    throw "sqlcmd not found on PATH. Install SQL Server Command Line Utilities."
}

Write-Host "Server : $ServerInstance" -ForegroundColor Cyan
Write-Host "Database: $Database" -ForegroundColor Cyan

# Verify the database actually exists and is reachable.
$dbCheck = sqlcmd -S $ServerInstance -d master -E -C -Q "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name = '$Database';" -W -h -1
if (-not ($dbCheck -match "$Database")) {
    throw "Database '$Database' not found on '$ServerInstance'."
}

# ---------------------------------------------------------------------------
# Deletion plan: leaf tables first, in FK-safe order.
# ---------------------------------------------------------------------------
# Ordered by dependency depth (children before parents). Each table name is
# followed by an inline estimate for the pre-delete report.
$DeleteOrder = @(
    "NotificationAttempt",     # -> NotificationDelivery
    "NotificationDelivery",    # -> Notification, UserDevice
    "Notification",            # -> NotificationEvent, User
    "NotificationEvent",       # -> User
    "NotificationPreference",  # -> User
    "NotificationTemplate",    # seed data; re-created on startup
    "RefreshToken",            # -> User, UserSession
    "UserSession",             # -> User
    "UserDevice",              # -> User
    "DeliveryOtp",             # -> Delivery
    "DeliveryLocation",        # -> Delivery (cascade, explicit for clarity)
    "DeliveryAssignment",      # -> Delivery, User
    "MilkTestImage",           # -> MilkTest, User
    "MilkTestParameter",       # -> MilkTest
    "MilkTest",                # -> Branch, Delivery, User
    "MilkUsage",               # -> MilkBatch, Branch, User
    "MilkBatch",               # -> Branch, MilkProduction
    "MilkProduction",          # -> Branch, User
    "Delivery",                # -> Branch, Order, SubscriptionDelivery, User
    "OrderItem",               # -> Order (cascade), Product
    "WalletTransaction",       # -> Order, Payment, Subscription, Wallet, User
    "Refund",                  # -> Payment, User
    "Payment",                 # -> Order, Subscription, User
    "PaymentWebhook",          # standalone
    "SubscriptionSchedule",    # -> Subscription
    "SubscriptionDelivery",    # -> Branch, Subscription
    "Subscription",            # -> Branch, CustomerAddress, Product, User
    "Order",                   # -> Branch, CustomerAddress, User
    "CustomerAddress",         # -> User
    "CustomerProfile",         # -> User
    "Wallet",                  # -> User
    "EmployeeInvitation",      # -> Branch, User
    "CameraStream",            # -> Camera, User
    "Camera",                  # -> Branch
    "ProductBranch",           # -> Product, Branch
    "Product",                 # -> ProductCategory
    "ProductCategory",         # seed data; re-created on startup
    "NumberSeries",            # -> User (CreatedByUserId/UpdatedByUserId); re-seeded
    "UserRole",                # -> User, Role
    "User",                    # -> (all children removed above)
    "RolePermission",          # -> Role, Permission; re-seeded
    "Permission",              # re-seeded
    "Role",                    # re-seeded
    "Branch",                  # re-seeded (MAIN)
    "OtpChallenge",            # standalone
    "AuditLog",                # standalone
    "SystemConfiguration"      # standalone
)

# ---------------------------------------------------------------------------
# Dry-run: report current row counts for every table in the plan.
# ---------------------------------------------------------------------------
function Get-RowCounts([string[]]$Tables) {
    $counts = @{}
    foreach ($t in $Tables) {
        $n = sqlcmd -S $ServerInstance -d $Database -E -C -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.[$t];" -W -h -1
        $n = ($n | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
        if ($null -eq $n -or $n -eq "") { $n = 0 }
        $counts[$t] = [int]$n
    }
    return $counts
}

Write-Host "`n=== DELETION PLAN (dry-run) ===" -ForegroundColor Yellow
$before = Get-RowCounts $DeleteOrder
$totalBefore = 0
foreach ($t in $DeleteOrder) {
    $totalBefore += $before[$t]
    $status = if ($before[$t] -eq 0) { " " } else { "*" }
    Write-Host ("{0} {1,-28} {2,6}" -f $status, $t, $before[$t])
}
Write-Host ("".PadRight(45, '-'))
Write-Host ("TOTAL to delete: {0} rows" -f $totalBefore) -ForegroundColor Cyan
Write-Host ("Retained after API restart (re-seeded): Roles, Permissions, RolePermissions, Branch(MAIN), NumberSeries, ProductCategory(MILK), Product(FRESH-BUFFALO-MILK), NotificationTemplate, and the 8 Development/UAT users.") -ForegroundColor DarkGray

if (-not $Confirm) {
    Write-Host "`nDRY-RUN: no changes made. Re-run with -Confirm to execute." -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------------------
# Confirmation prompt before destructive action.
# ---------------------------------------------------------------------------
Write-Host "`nWARNING: This will permanently delete $totalBefore rows from '$Database'." -ForegroundColor Red
if (-not $Force) {
    $answer = Read-Host "Type 'RESET' to continue"
    if ($answer -ne "RESET") {
        Write-Host "Aborted. No changes made." -ForegroundColor Yellow
        exit 1
    }
}
else {
    Write-Host "Automated run (-Force): proceeding without interactive confirmation." -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------------------
# Execute deletions in a single transaction.
# ---------------------------------------------------------------------------
$sql = "SET XACT_ABORT ON; SET QUOTED_IDENTIFIER ON; BEGIN TRAN;"
foreach ($t in $DeleteOrder) {
    if ($before[$t] -gt 0) {
        $sql += " DELETE FROM dbo.[$t];"
    }
}
$sql += " COMMIT TRAN;"

Write-Host "`n=== EXECUTING RESET ===" -ForegroundColor Yellow
sqlcmd -S $ServerInstance -d $Database -E -C -b -Q $sql
if ($LASTEXITCODE -ne 0) {
    throw "sqlcmd failed (exit code $LASTEXITCODE). Transaction rolled back; no changes persisted."
}

# ---------------------------------------------------------------------------
# Post-reset verification.
# ---------------------------------------------------------------------------
Write-Host "`n=== POST-RESET VERIFICATION ===" -ForegroundColor Yellow
$after = Get-RowCounts $DeleteOrder
$fail = $false
$totalAfter = 0
foreach ($t in $DeleteOrder) {
    $totalAfter += $after[$t]
    if ($after[$t] -ne 0) {
        $fail = $true
        Write-Host ("[FAIL] {0,-28} {1}" -f $t, $after[$t]) -ForegroundColor Red
    } else {
        Write-Host ("[OK]   {0,-28} 0" -f $t) -ForegroundColor Green
    }
}
Write-Host ("".PadRight(45, '-'))
Write-Host ("TOTAL rows remaining in deleted tables: {0}" -f $totalAfter) -ForegroundColor Cyan

if ($fail) {
    Write-Host "`nRESET COMPLETED WITH RESIDUALS - investigate the [FAIL] rows above." -ForegroundColor Red
    exit 2
}

Write-Host "`nRESET COMPLETE. Restart the API (Development) to re-seed reference data and the 8 UAT login accounts." -ForegroundColor Green
exit 0
