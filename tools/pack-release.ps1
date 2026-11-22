# Builds the release zip, publish\release\Nikomi-<version>-win-x64.zip, and prints its SHA-256.
#
#   powershell -File tools\pack-release.ps1
#
# The zip holds one Nikomi folder with the published app (Nikomi.exe, NikomiCli.exe and what they load),
# without debug symbols. Its entry names use forward slashes: winget's archive check rejects a zip with
# backslashes in them as malware, which happened to the hand-made 1.0.0 zip. The version comes from
# Directory.Build.props. Runs in Windows PowerShell 5.1 and in PowerShell 7.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No Version in Directory.Build.props' }

$out = Join-Path $repo 'publish\release'
$folder = Join-Path $out 'Nikomi'
$zip = Join-Path $out "Nikomi-$version-win-x64.zip"

# A clean folder, so no file left over from an earlier build ends up in the zip.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $repo 'src\Nikomi\Nikomi.csproj') -c Release -o $folder
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
foreach ($exe in 'Nikomi.exe', 'NikomiCli.exe') {
    if (-not (Test-Path (Join-Path $folder $exe))) { throw "$exe is missing from the published folder" }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$files = Get-ChildItem -Recurse -File $folder | Where-Object { $_.Extension -ne '.pdb' } | Sort-Object FullName
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in $files) {
            $name = 'Nikomi/' + $f.FullName.Substring($folder.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $f.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

# Read it back: every file is there, under forward-slash names.
$check = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($check.Entries | ForEach-Object { $_.FullName })
    $backslash = @($names | Where-Object { $_.Contains('\') })
    if ($backslash.Count -gt 0) { throw "Entry names with a backslash: $($backslash[0])" }
    if ($names.Count -ne $files.Count) { throw "The zip holds $($names.Count) files, the folder $($files.Count)" }
    foreach ($exe in 'Nikomi/Nikomi.exe', 'Nikomi/NikomiCli.exe') {
        if ($names -notcontains $exe) { throw "$exe is missing from the zip" }
    }
}
finally { $check.Dispose() }

$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Nikomi $version, $($files.Count) files, $size MB"
Write-Host "  $zip"
Write-Host "  SHA-256 $hash"
Write-Host 'Attach the zip to the GitHub release, put the SHA-256 in the release notes, and use it as'
Write-Host 'InstallerSha256 in the winget manifest (winget\SUBMIT.md on feature/winget, "Later versions").'
