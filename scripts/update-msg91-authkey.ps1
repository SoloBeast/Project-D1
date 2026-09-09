# Temporary helper: login as Owner and update MSG91 AuthKey + WidgetId through
# the admin endpoint (which encrypts AuthKey via Data Protection before storage).
$ErrorActionPreference = 'Stop'

$base = 'http://localhost:5210'

$loginBody = @{
    login    = 'owner@doodhdirect.local'
    password = 'DoodhDirect@123'
    device   = @{
        deviceIdentifier = 'msg91-setup-cli'
        deviceName       = 'CLI'
        platform         = 'windows'
    }
} | ConvertTo-Json -Depth 5

$login = Invoke-RestMethod -Method Post -Uri "$base/api/v1/auth/login" -ContentType 'application/json' -Body $loginBody
$token = $login.data.tokens.accessToken
Write-Host "LOGIN_OK user=$($login.data.user.email) roles=$($login.data.user.roles -join ',')"

$headers = @{ Authorization = "Bearer $token" }
$putBody = @{
    widgetId = '366845684d37373433363032'
    authKey  = '566132AMJfTnmD6a9727d6P1'
    environment = 'Production'
} | ConvertTo-Json

$result = Invoke-RestMethod -Method Put -Uri "$base/api/v1/admin/setup/otp-provider" -ContentType 'application/json' -Headers $headers -Body $putBody
Write-Host "PUT_OK"
$result | ConvertTo-Json -Depth 6
