param([switch]$SkipArchive)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Get-Command python, gitleaks -ErrorAction Stop | Out-Null
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$output = Join-Path $root ('artifacts/open-source/' + $stamp)
$source = Join-Path $output 'LiveCaptions-Translator'
New-Item -ItemType Directory -Path $source | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $root 'public-source.json') -Raw | ConvertFrom-Json
foreach ($name in $manifest.files) {
    if ([IO.Path]::IsPathRooted($name) -or $name -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid export path.' }
    $origin = [IO.Path]::GetFullPath((Join-Path $root $name))
    $target = [IO.Path]::GetFullPath((Join-Path $source $name))
    if (-not $origin.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Export path escapes root.' }
    $item = Get-Item -LiteralPath $origin
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Export accepts regular files only.' }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $origin -Destination $target
}
& python (Join-Path $root 'scripts/check-public-source.py') --root $source --private-root $root --report (Join-Path $output 'privacy-report.json')
if ($LASTEXITCODE -ne 0) { throw 'Public source privacy checks failed; no archive created.' }
& gitleaks dir $source --redact=100 --no-banner --no-color --report-format json --report-path (Join-Path $output 'gitleaks.json')
if ($LASTEXITCODE -ne 0) { throw 'Public source secret scan failed; no archive created.' }
if (-not $SkipArchive) {
    # CreateFromDirectory includes dotfiles; Compress-Archive can omit hidden files.
    $archive = Join-Path $output 'LiveCaptionsTranslator-source.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($source, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
    try { foreach ($entry in $zip.Entries) { $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero) } }
    finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  LiveCaptionsTranslator-source.zip" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
}
Write-Output "PUBLIC_SOURCE=$source"
Write-Output "PUBLIC_OUTPUT=$output"
