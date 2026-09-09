# Mint a dev HS256 JWT for the UAT customer (user 33) using the appsettings dev signing key.
$ErrorActionPreference = 'Stop'

$signingKey = 'CONFIGURE_A_SECRET_OF_AT_LEAST_32_CHARACTERS'
$issuer = 'DoodhDirect.Api'
$audience = 'DoodhDirect.App'

$nowUtc = [DateTime]::UtcNow
$nbf = [DateTimeOffset]::new($nowUtc).ToUnixTimeSeconds()
$exp = [DateTimeOffset]::new($nowUtc.AddHours(1)).ToUnixTimeSeconds()

$permissions = @(
    'IDENTITY.PROFILE.READ_OWN','IDENTITY.PROFILE.UPDATE_OWN','IDENTITY.SESSIONS.MANAGE_OWN',
    'ORDERS.CREATE_OWN','ORDERS.READ_OWN','ORDERS.CANCEL_OWN',
    'PAYMENTS.CREATE_OWN','PAYMENTS.READ_OWN',
    'WALLET.READ_OWN','WALLET.TOPUP_OWN',
    'SUBSCRIPTIONS.CREATE_OWN','SUBSCRIPTIONS.READ_OWN','SUBSCRIPTIONS.MANAGE_OWN',
    'DELIVERIES.READ_OWN',
    'MILK_TESTS.REQUEST_OWN','MILK_TESTS.READ_OWN','MILK_TESTS.DECIDE_OWN',
    'CAMERAS.VIEW_PUBLIC'
)

$payload = [ordered]@{
    sub        = '37D801C8-F008-48D4-93C4-6878A1935260'
    user_id    = '33'
    session_id = [Guid]::NewGuid().ToString()
    jti        = [Guid]::NewGuid().ToString('N')
    name       = 'hemant.sharma1209s@gmail.com'
    role       = 'CUSTOMER'
    permission = $permissions
    nbf        = $nbf
    exp        = $exp
    iss        = $issuer
    aud        = $audience
}

function To-Base64Url([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}

$headerJson = '{"alg":"HS256","typ":"JWT"}'
$headerB64 = To-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($headerJson))
$payloadJson = $payload | ConvertTo-Json -Compress -Depth 5
$payloadB64 = To-Base64Url ([System.Text.Encoding]::UTF8.GetBytes($payloadJson))

$signingInput = "$headerB64.$payloadB64"
$hmac = [System.Security.Cryptography.HMACSHA256]::new([System.Text.Encoding]::UTF8.GetBytes($signingKey))
$sigB64 = To-Base64Url ($hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($signingInput)))

$token = "$signingInput.$sigB64"
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'customer-jwt.txt'), $token)
Write-Host $token
