param([switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root ('artifacts/portable/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
$app = Join-Path $output 'LiveCaptionsTranslator-win-x64'
New-Item -ItemType Directory -Path $app | Out-Null
$publish = @('publish', (Join-Path $root 'LiveCaptionsTranslator.csproj'), '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:PathMap=$root=/_/src", '-o', $app)
if ($NoRestore) { $publish += '--no-restore' }
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Portable build failed.' }
# Only public templates are copied; never use the active user profile.
Copy-Item -LiteralPath (Join-Path $root 'setting.portable.example.json') -Destination (Join-Path $app 'setting.json')
Copy-Item -LiteralPath (Join-Path $root 'glossary.example.txt') -Destination (Join-Path $app 'glossary.txt')
foreach ($name in @('LICENSE', 'NOTICE', 'CHANGES.md', 'README.md', 'README_zh-CN.md', 'README_Portability.md',
    'PRIVACY.md', 'SECURITY.md', 'CONTRIBUTING.md', 'OPEN_SOURCE_RELEASE.md', 'THIRD_PARTY_NOTICES.md', '.env.example')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $app $name)
}
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $app 'licenses') -Recurse
# Preserve license files for the exact self-contained runtime packages restored by the SDK.
$assets = Get-Content -LiteralPath (Join-Path $root 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$runtimePackages = @($assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } |
    Where-Object { $_.name -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') })
foreach ($package in $runtimePackages) {
    $version = $package.version.Trim('[',']').Split(',')[0].Trim()
    $relative = $package.name.ToLowerInvariant() + '/' + $version
    $found = $false
    foreach ($base in $assets.packageFolders.Keys) {
        $directory = Join-Path $base $relative
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        $noticeFiles = @(Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)' })
        if ($noticeFiles.Count -eq 0) { continue }
        $destination = Join-Path $app ('licenses/runtime/' + $relative)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $noticeFiles | Copy-Item -Destination $destination
        $found = $true
        break
    }
    if (-not $found) { throw 'Runtime license files missing.' }
}
if ($runtimePackages.Count -lt 2) { throw 'Could not determine both runtime license packages.' }
New-Item -ItemType Directory -Path (Join-Path $app 'models') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'models/README.md') -Destination (Join-Path $app 'models/README.md')
@'
@echo off
cd /d "%~dp0"
start "" "%~dp0LiveCaptionsTranslator.exe"
'@ | Set-Content -LiteralPath (Join-Path $app 'start.bat') -Encoding ascii
if (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Extension -in @('.pdb','.wav','.onnx','.db','.jsonl','.log') -or $_.Name -eq '.env' }) {
    throw 'Unexpected private or debug file in portable output.'
}
$archive = Join-Path $output 'LiveCaptionsTranslator-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($app, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  LiveCaptionsTranslator-win-x64.zip" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "PORTABLE_OUTPUT=$output"
