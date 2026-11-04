[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$pythonVersion = '3.10.11'
$archiveName = "python-$pythonVersion-embed-amd64.zip"
$archiveUrl = "https://www.python.org/ftp/python/$pythonVersion/$archiveName"
$archiveSha256 = '608619F8619075629C9C69F361352A0DA6ED7E62F83A0E19C63E0EA32EB7629D'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$requirementsPath = Join-Path $PSScriptRoot 'python-requirements.txt'
$cacheDirectory = Join-Path $repositoryRoot '.build-cache'
$archivePath = Join-Path $cacheDirectory $archiveName
$getPipPath = Join-Path $cacheDirectory 'get-pip.py'
$destinationFull = [System.IO.Path]::GetFullPath($Destination)
$stagingPath = "$destinationFull.staging-$PID"
$markerName = '.beatzonkeys-python.json'

function Invoke-NativeChecked {
    param(
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Assert-SafeDestination {
    param([string] $Path)

    $root = [System.IO.Path]::GetPathRoot($Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -eq $root -or $Path.Length -lt 8) {
        throw "Refusing unsafe Python destination: '$Path'."
    }

    if ($Path -eq [System.IO.Path]::GetFullPath($repositoryRoot)) {
        throw 'The Python destination cannot be the repository root.'
    }
}

Assert-SafeDestination -Path $destinationFull
New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null

$requirementsHash = (Get-FileHash -LiteralPath $requirementsPath -Algorithm SHA256).Hash
$markerPath = Join-Path $destinationFull $markerName
$pythonExecutable = Join-Path $destinationFull 'python.exe'
if (-not $Force -and (Test-Path -LiteralPath $pythonExecutable) -and (Test-Path -LiteralPath $markerPath)) {
    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($marker.pythonVersion -eq $pythonVersion -and $marker.requirementsSha256 -eq $requirementsHash) {
            Write-Host "Portable Python $pythonVersion is already current: $destinationFull"
            Invoke-NativeChecked $pythonExecutable '-c' 'import basic_pitch, onnxruntime, librosa, pretty_midi; print(basic_pitch.__file__)'
            exit 0
        }
    }
    catch {
        Write-Warning "Existing runtime validation failed; it will be rebuilt. $($_.Exception.Message)"
    }
}

if (-not (Test-Path -LiteralPath $archivePath) -or
    (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveSha256) {
    Write-Host "Downloading official CPython $pythonVersion embeddable package..."
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing
}

$actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($actualHash -ne $archiveSha256) {
    throw "CPython archive checksum mismatch. Expected $archiveSha256, got $actualHash."
}

if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

try {
    Write-Host 'Extracting portable Python...'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingPath -Force

    $pthPath = Get-ChildItem -LiteralPath $stagingPath -Filter 'python*._pth' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $pthPath) {
        throw 'The CPython embeddable archive contains no python*._pth file.'
    }

    $pthLines = Get-Content -LiteralPath $pthPath
    $pthLines = $pthLines | ForEach-Object {
        if ($_ -eq '#import site') { 'import site' } else { $_ }
    }
    if ($pthLines -notcontains 'Lib\site-packages') {
        $pthLines += 'Lib\site-packages'
    }
    Set-Content -LiteralPath $pthPath -Value $pthLines -Encoding ASCII
    New-Item -ItemType Directory -Path (Join-Path $stagingPath 'Lib\site-packages') -Force | Out-Null

    if (-not (Test-Path -LiteralPath $getPipPath)) {
        Write-Host 'Downloading the official pip bootstrap...'
        Invoke-WebRequest -Uri 'https://bootstrap.pypa.io/pip/3.10/get-pip.py' -OutFile $getPipPath -UseBasicParsing
    }

    $stagingPython = Join-Path $stagingPath 'python.exe'
    $env:PYTHONUTF8 = '1'
    $env:PYTHONNOUSERSITE = '1'
    Write-Host 'Installing pip into the embedded runtime...'
    Invoke-NativeChecked $stagingPython $getPipPath '--disable-pip-version-check' '--no-warn-script-location'

    Write-Host 'Installing Basic Pitch and the ONNX runtime...'
    Invoke-NativeChecked $stagingPython '-m' 'pip' 'install' '--disable-pip-version-check' '--no-cache-dir' '--no-warn-script-location' '-r' $requirementsPath

    Write-Host 'Validating imports and the packaged Basic Pitch model...'
    Invoke-NativeChecked $stagingPython '-c' 'from pathlib import Path; import basic_pitch, onnxruntime, librosa, pretty_midi; from basic_pitch import ICASSP_2022_MODEL_PATH; assert Path(ICASSP_2022_MODEL_PATH).exists(); print(ICASSP_2022_MODEL_PATH)'

    [ordered]@{
        pythonVersion = $pythonVersion
        basicPitchVersion = '0.4.0'
        modelRuntime = 'ONNX Runtime 1.18.1'
        requirementsSha256 = $requirementsHash
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stagingPath $markerName) -Encoding UTF8

    if (Test-Path -LiteralPath $destinationFull) {
        Remove-Item -LiteralPath $destinationFull -Recurse -Force
    }
    Move-Item -LiteralPath $stagingPath -Destination $destinationFull
    Write-Host "Portable Basic Pitch runtime is ready: $destinationFull"
}
catch {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
    throw
}
