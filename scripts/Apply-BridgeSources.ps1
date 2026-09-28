$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = 'C:\Users\Anastasia\OneDrive\Desktop\BimSAgent\RevitAddin'
$handler = Join-Path $target 'RevitReadHandler.cs'
$expected = '1866BE59C83206FE6A435BD5DD6B871D297C2CA0D9DB8DDFCF0C5536102641A7'
if ((Get-FileHash -LiteralPath $handler -Algorithm SHA256).Hash -ne $expected) {
    throw 'RevitReadHandler has changed. Review the diff before applying; no files overwritten.'
}
foreach ($name in @('ViewGraphRequest.cs', 'ViewGraphReader.cs')) {
    if (Test-Path -LiteralPath (Join-Path $target $name)) { throw "File already exists: $name. No files overwritten." }
}
$backup = Join-Path $workspace 'artifacts\bridge-backup'
New-Item -ItemType Directory -Path $backup -Force | Out-Null
Copy-Item -LiteralPath $handler -Destination (Join-Path $backup 'RevitReadHandler.original.cs')
foreach ($name in @('ViewGraphRequest.cs', 'ViewGraphReader.cs', 'RevitReadHandler.cs')) {
    Copy-Item -LiteralPath (Join-Path $workspace "bridge\$name") -Destination (Join-Path $target $name)
}
Write-Output 'Three bridge source files updated. No DLL deployed; Revit was not restarted.'
