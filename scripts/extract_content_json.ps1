$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path 'scripts\docs_content' | Out-Null

function Wrap([string]$text, [int]$width = 130) {
  $sb = New-Object System.Text.StringBuilder
  for ($i = 0; $i -lt $text.Length; $i += $width) {
    $len = [Math]::Min($width, $text.Length - $i)
    [void]$sb.AppendLine($text.Substring($i, $len))
  }
  return $sb.ToString()
}

function SafeSub([string]$s, [int]$start, [int]$len) {
  if ($start -lt 0) { $start = 0 }
  if ($start -ge $s.Length) { return "" }
  $len = [Math]::Min($len, $s.Length - $start)
  return $s.Substring($start, $len)
}

$pages = @(
  @{ slug = 'send';             file = 'otp-widget-send-docs.html' },
  @{ slug = 'retry';            file = 'otp-widget-retry-docs.html' },
  @{ slug = 'verifyotp';        file = 'otp-verify-docs.html' },
  @{ slug = 'verifyaccesstoken'; file = 'otp-verify-access-docs.html' }
)

foreach ($p in $pages) {
  $s = Get-Content -Raw $p.file
  $dv = $s.IndexOf('docViewData')
  Write-Output ("=== " + $p.file + " LEN=" + $s.Length + " docViewData at " + $dv)

  # Window BEFORE docViewData: capture host/method/body/params/headers + urlName
  $pre = SafeSub $s ($dv - 4000) 4200
  # Window AFTER docViewData: sampleResponse etc.
  $post = SafeSub $s $dv 3800

  $out = "scripts\docs_content\" + $p.slug + "_json.txt"
  Set-Content -Path $out -Value (Wrap $pre)
  Add-Content -Path $out -Value "`n`n===== AFTER docViewData =====`n"
  Add-Content -Path $out -Value (Wrap $post)
  Write-Output ("  wrote " + $out)
}
Write-Output "DONE"
