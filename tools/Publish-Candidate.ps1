param([string]$CandidateId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
if ($CandidateId -notmatch '^\d{8}-\d{6}$') { throw 'CandidateId must be yyyyMMdd-HHmmss' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$candidateRoot = Join-Path $projectRoot "artifacts/candidates/$CandidateId"
if (Test-Path -LiteralPath $candidateRoot) { throw 'Candidate directory already exists; existing evidence is preserved' }
$packageRoot = Join-Path $candidateRoot 'NtfyTray'
$verificationRoot = Join-Path $candidateRoot 'verification'
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'New-TrayIcons.ps1')
    & dotnet restore --locked-mode *> (Join-Path $verificationRoot 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed; inspect verification/restore.log' }
    & dotnet build -c Release --no-restore *> (Join-Path $verificationRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed; inspect verification/build.log' }
    & dotnet test -c Release --no-build --logger 'trx;LogFileName=tests.trx' --results-directory $verificationRoot *> (Join-Path $verificationRoot 'test.log')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; inspect verification/test.log' }
    & dotnet publish src/NtfyTray/NtfyTray.csproj -c Release -r win-x64 --self-contained false --no-restore -o $packageRoot *> (Join-Path $verificationRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; inspect verification/publish.log' }

    if (Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter config.yaml) { throw 'Real config.yaml must never be packaged' }
    foreach ($required in @('NtfyTray.exe','NtfyTray.dll','README.md','config.example.yaml','Assets/app.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required))) { throw "Missing package file: $required" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $packageRoot 'NtfyTray.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not ($runtime.runtimeOptions.frameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App')) { throw 'Package must reference the system Desktop Runtime' }
    $unwanted = Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Name -match '^(coreclr|hostfxr|hostpolicy|clrjit|PresentationFramework|System\.Private\.CoreLib|Microsoft\.Windows\.AI\.|Microsoft\.ML\.|Microsoft\.UI\.Xaml|WebView2Loader)' -or $_.Name -like '*.resources.dll' -or $_.Extension -eq '.pdb' }
    if ($unwanted) { throw ('Unexpected framework/unused resource in slim package: ' + (($unwanted.Name) -join ', ')) }

    # The local test configuration is excluded. Compare the actual local secret without printing it.
    $localConfig = Join-Path $projectRoot 'config.yaml'
    if (Test-Path -LiteralPath $localConfig) {
        $match = [regex]::Match([IO.File]::ReadAllText($localConfig), '(?m)^\s*token:\s*"([^"]+)"\s*$')
        if ($match.Success) {
            foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Where-Object { $_.Extension -in '.yaml','.json','.md','.txt','.config' -or $_.Name -eq 'NtfyTray.dll' })) {
                if ([IO.File]::ReadAllText($file.FullName).Contains($match.Groups[1].Value)) { throw "Private credential found in package file: $($file.Name)" }
            }
        }
    }

    function Get-Fingerprints([string]$basePath, $files) {
        @($files | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path=[IO.Path]::GetRelativePath($basePath,$_.FullName).Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    }
    $runtimeFiles = Get-Fingerprints $packageRoot (Get-ChildItem -LiteralPath $packageRoot -File -Recurse)
    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests'),(Join-Path $projectRoot 'tools') -File -Recurse | Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' -and $_.Extension -ne '.user' -and $_.Name -ne 'config.yaml' })
    $sourceFiles += Get-Item -LiteralPath 'NtfyTray.slnx','global.json','README.md','config.example.yaml','connected.png','disconnected.png','.gitignore'
    $zipPath = Join-Path $candidateRoot 'NtfyTray-win-x64.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot,$zipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
    $expandedRoot = Join-Path $candidateRoot 'expanded'
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$expandedRoot)
    $expandedFiles = Get-Fingerprints $expandedRoot (Get-ChildItem -LiteralPath $expandedRoot -File -Recurse)
    if (($runtimeFiles | ConvertTo-Json -Depth 5 -Compress) -ne ($expandedFiles | ConvertTo-Json -Depth 5 -Compress)) { throw 'ZIP extraction fingerprints do not match publish output' }
    [xml]$testResults = Get-Content -LiteralPath (Join-Path $verificationRoot 'tests.trx') -Raw
    $counters = $testResults.GetElementsByTagName('Counters')[0]
    $manifest = [ordered]@{
        candidate=$CandidateId
        status='pending-user-acceptance-and-live-validation'
        createdUtc=[DateTimeOffset]::UtcNow.ToString('O')
        sourceBaseline='No Git repository; SHA-256 source fingerprints below'
        sourceFiles=(Get-Fingerprints $projectRoot $sourceFiles)
        windowsAppSdk='2.4.0'
        runtimeOptions=$runtime.runtimeOptions
        commands=@('dotnet restore --locked-mode','dotnet build -c Release --no-restore','dotnet test -c Release --no-build --logger trx','dotnet publish src/NtfyTray/NtfyTray.csproj -c Release -r win-x64 --self-contained false --no-restore')
        tests=@{total=[int]$counters.total; passed=[int]$counters.passed; failed=[int]$counters.failed}
        zip=@{file='NtfyTray-win-x64.zip';sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash;bytes=(Get-Item -LiteralPath $zipPath).Length}
        runtimeFiles=$runtimeFiles
        extractedFiles=$expandedFiles
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $candidateRoot 'candidate-manifest.json') -Encoding utf8
    Write-Output "Candidate: $candidateRoot"
    Write-Output "Tests: $($counters.passed)/$($counters.total)"
    Write-Output "ZIP SHA-256: $($manifest.zip.sha256)"
} finally { Pop-Location }

