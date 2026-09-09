$ErrorActionPreference = 'Stop'
$s = Get-Content -Raw 'otp-verify-access-docs.html'
Write-Output ("FLIGHT_LEN=" + $s.Length)

# --- 1. all occurrences of verify-access-token (plain) ---
$pat = 'verify-access-token'
$start = 0
$count = 0
while (($i = $s.IndexOf($pat, $start)) -ge 0) {
  $count++
  Write-Output ("OCC#" + $count + " at " + $i)
  $start = $i + $pat.Length
}
Write-Output ("TOTAL_VERIFY_ACCESS_TOKEN=" + $count)

# --- 2. page-tree entry via plain name ---
foreach ($needle in @('"name":"Verify Access Token"', '\"name\":\"Verify Access Token\"', 'Verify Access Token')) {
  $i = $s.IndexOf($needle)
  Write-Output ("NEEDLE=" + $needle + " IDX=" + $i)
  if ($i -ge 0) {
    $st = [Math]::Max(0, $i - 600)
    $ln = [Math]::Min(1800, $s.Length - $st)
    Write-Output $s.Substring($st, $ln)
    break
  }
}

# --- 3. page-tree entry via urlName plain ---
foreach ($needle in @('"urlName":"verify-access-token"', '\"urlName\":\"verify-access-token\"')) {
  $i = $s.IndexOf($needle)
  Write-Output ("NEEDLE=" + $needle + " IDX=" + $i)
  if ($i -ge 0) {
    $st = [Math]::Max(0, $i - 400)
    $ln = [Math]::Min(1200, $s.Length - $st)
    Write-Output $s.Substring($st, $ln)
    break
  }
}

# --- 4. verify the OTP Widget section children list (find send-otp-1 and walk back) ---
$i = $s.IndexOf('"urlName":"send-otp-1"')
if ($i -lt 0) { $i = $s.IndexOf('\u0022urlName\u0022:\u0022send-otp-1\u0022') }
Write-Output ("SENDOTP_IDX=" + $i)
if ($i -ge 0) {
  $st = [Math]::Max(0, $i - 200)
  $ln = [Math]::Min(1000, $s.Length - $st)
  Write-Output $s.Substring($st, $ln)
}

# --- 5. collectionData reference location ---
$i = $s.IndexOf('collectionData')
Write-Output ("COLLECTIONDATA_IDX=" + $i)
if ($i -ge 0) {
  $st = [Math]::Max(0, $i - 300)
  $ln = [Math]::Min(900, $s.Length - $st)
  Write-Output $s.Substring($st, $ln)
}
