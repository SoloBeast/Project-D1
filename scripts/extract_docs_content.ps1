$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path 'scripts\docs_content' | Out-Null

$pages = @(
  @{ slug = 'send';            file = 'otp-widget-send-docs.html'; id = 'kieYYJ9MdH9j' },
  @{ slug = 'retry';           file = 'otp-widget-retry-docs.html'; id = 'a1ap6iBXkd6-' },
  @{ slug = 'verifyotp';       file = 'otp-verify-docs.html';       id = 'zakqFeIRg-d9' },
  @{ slug = 'verifyaccesstoken'; file = 'otp-verify-access-docs.html'; id = 'vQOqL-oP2gro' }
)

foreach ($p in $pages) {
  $s = Get-Content -Raw $p.file
  $out = "scripts\docs_content\" + $p.slug + ".txt"
  Write-Output ("=== " + $p.file + " LEN=" + $s.Length + " ===")

  # 1) sidebar tree entry for this page id
  $idPat = '"' + $p.id + '"'
  $i1 = $s.IndexOf($idPat)
  if ($i1 -lt 0) { $i1 = $s.IndexOf('\"' + $p.id + '\"') }
  Write-Output ("  ID first at " + $i1)

  # 2) docViewData occurrences (content objects)
  $pat = 'docViewData'
  $start = 0
  $n = 0
  while (($i = $s.IndexOf($pat, $start)) -ge 0) {
    $n++
    Write-Output ("  docViewData occ#" + $n + " at " + $i)
    $st = [Math]::Max(0, $i - 9000)
    $ln = [Math]::Min(11500, $s.Length - $st)
    Add-Content -Path $out -Value ("===== docViewData occ#" + $n + " at " + $i + " =====")
    Add-Content -Path $out -Value $s.Substring($st, $ln)
    $start = $i + 1
  }
  Write-Output ("  docViewData total = " + $n)
}
Write-Output "DONE"
