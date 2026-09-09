# UAT scenario: manual usage must reduce availability exactly once.
# Before: 1 batch (20L), 0 usages -> availability 20L
# Action: record manual usage 2L on batch DEA1FF04-3DDE-4E70-A6D6-741F9ACFB2F9
# Expect: availability 18L (produced 20 - consumed 2), NOT 16L (which would mean double counting)
$ErrorActionPreference = "Stop"
$base = "http://localhost:5209"

$body = @{
    login    = "919354816929"
    password = "DoodhDirect@123"
    device   = @{ deviceIdentifier = "uat-manual-usage"; deviceName = "PowerShell UAT"; platform = "powershell" }
} | ConvertTo-Json -Depth 5
$login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType "application/json" -Body $body
$token = $login.data.tokens.accessToken
$headers = @{ Authorization = "Bearer $token" }

# 1) availability before
$before = Invoke-RestMethod -Method Get -Uri "$base/api/v1/dairy/branches/6/availability" -Headers $headers
Write-Host ("BEFORE usage: produced={0} used={1} available={2}" -f $before.data.quantityProduced, $before.data.quantityUsed, $before.data.availableQuantity)

# 2) record manual usage 2L on the batch (batchId is in URL; body is RecordMilkUsageRequest)
$usageBody = @{
    usedAt       = (Get-Date).ToUniversalTime().ToString("o")
    quantityUsed = 2
    purpose      = "UAT manual usage"
    remarks      = "live UAT: manual usage reduces availability exactly once"
} | ConvertTo-Json -Depth 3
try {
    $usage = Invoke-RestMethod -Method Post -Uri "$base/api/v1/dairy/batches/DEA1FF04-3DDE-4E70-A6D6-741F9ACFB2F9/usage" -Headers $headers -ContentType "application/json" -Body $usageBody
    Write-Host ("USAGE recorded: {0}" -f ($usage.data | ConvertTo-Json -Depth 6 -Compress))
}
catch {
    Write-Host ("USAGE FAILED: {0}" -f $_.ErrorDetails.Message)
    Write-Host ("Try body: {0}" -f $usageBody)
    exit 1
}

# 3) availability after
$after = Invoke-RestMethod -Method Get -Uri "$base/api/v1/dairy/branches/6/availability" -Headers $headers
Write-Host ("AFTER usage: produced={0} used={1} available={2}" -f $after.data.quantityProduced, $after.data.quantityUsed, $after.data.availableQuantity)

$result = @{
    scenario = "manual-usage-reduces-availability-once"
    before   = $before.data
    after    = $after.data
} | ConvertTo-Json -Depth 8
$result | Out-File -FilePath "scripts/uat-manual-usage-result.json" -Encoding utf8
Write-Host "Saved scripts/uat-manual-usage-result.json"
