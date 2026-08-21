[CmdletBinding()]
param(
    [int]$WebPort = 51482,
    [string]$ApiUrl = 'http://localhost:5209',
    [ValidateSet('chrome', 'web-server')]
    [string]$Device = 'chrome',
    [switch]$CheckConfiguration
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotenvPath = Join-Path $repositoryRoot '.env'
$mobilePath = Join-Path $repositoryRoot 'mobile'
$mapsKeyName = 'DOOHDIRECT_GOOGLE_MAPS_API_KEY'
$mapsKey = $null

if (Test-Path -LiteralPath $dotenvPath -PathType Leaf) {
    foreach ($line in [System.IO.File]::ReadLines($dotenvPath)) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#')) {
            continue
        }

        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $name = $trimmed.Substring(0, $separator).Trim()
        if ($name -cne $mapsKeyName) {
            continue
        }

        $value = $trimmed.Substring($separator + 1).Trim()
        if ($value.Length -ge 2) {
            $first = $value[0]
            $last = $value[$value.Length - 1]
            if (($first -eq '"' -and $last -eq '"') -or
                ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $mapsKey = $value
        }
        break
    }
}

$flutterArguments = @(
    'run',
    '-d', $Device,
    '--web-port', $WebPort.ToString(),
    "--dart-define=DOOHDIRECT_API_URL=$ApiUrl",
    '--dart-define=DOOHDIRECT_ENABLE_DEV_TOOLS=true'
)

if (-not [string]::IsNullOrWhiteSpace($mapsKey)) {
    $flutterArguments += "--dart-define=$mapsKeyName=$mapsKey"
} else {
    Write-Warning "$mapsKeyName is not configured in the repository-root .env. Flutter will retain its missing-key state."
}

$mapsDefinePrefix = "--dart-define=$mapsKeyName="
$mapsDefineArguments = @(
    $flutterArguments | Where-Object { $_.StartsWith($mapsDefinePrefix, [StringComparison]::Ordinal) }
)
if (-not [string]::IsNullOrWhiteSpace($mapsKey)) {
    if ($mapsDefineArguments.Count -ne 1 -or
        $mapsDefineArguments[0].Length -le $mapsDefinePrefix.Length) {
        throw "$mapsKeyName was loaded, but the Flutter argument handoff is missing or empty."
    }

    Write-Host "$mapsKeyName Dart define is present and non-empty. The value was not printed."
}

$redactedFlutterArguments = @(
    foreach ($argument in $flutterArguments) {
        if ($argument.StartsWith($mapsDefinePrefix, [StringComparison]::Ordinal)) {
            "$mapsDefinePrefix<REDACTED>"
        } else {
            $argument
        }
    }
)
Write-Host "Flutter working directory: $mobilePath"
Write-Host "Flutter command: flutter $($redactedFlutterArguments -join ' ')"

if ($CheckConfiguration) {
    if ([string]::IsNullOrWhiteSpace($mapsKey)) {
        exit 1
    }

    Write-Host "$mapsKeyName is configured for the Development launcher. The value was not printed."
    exit 0
}

Push-Location $mobilePath
try {
    & flutter @flutterArguments
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
