param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$Destination = (Join-Path $PSScriptRoot '..\templates')
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
if (!(Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw 'Source must be a directory.' }
if (Test-Path -LiteralPath $Destination) { throw 'Template destination already exists; inspect it before replacing samples.' }
$files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.xml')
if ($files.Count -eq 0) { throw 'No XML templates found.' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart('\')
    $target = Join-Path $Destination $relative
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target))
    [System.IO.File]::Copy($file.FullName, $target, $false)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) {
        throw "Template copy verification failed: $relative"
    }
}
"Copied and hash-verified $($files.Count) XML templates."
