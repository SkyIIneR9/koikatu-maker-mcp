param(
    [Parameter(Mandatory = $true)][string]$GameRoot,
    [string]$Python = 'python',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'Koikatu_Data\Managed\Assembly-CSharp.dll'))) {
    throw 'GameRoot must contain Koikatu_Data\Managed\Assembly-CSharp.dll.'
}
$venv = Join-Path $PSScriptRoot '.venv'
& $Python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'Python venv creation failed; install Python 3.13.' }
$venvPython = Join-Path $venv 'Scripts\python.exe'
& $venvPython -m pip install -r (Join-Path $PSScriptRoot 'requirements-lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }
& (Join-Path $PSScriptRoot 'build.ps1') -GameRoot $GameRoot -Install:$Install
# Print configuration for any MCP host; do not change the host's settings.
@{ mcpServers = @{ koikatu_maker = @{
    command = $venvPython
    args = @((Join-Path $PSScriptRoot 'server.py'))
    env = @{ KK_GAME_ROOT = $GameRoot }
} } } | ConvertTo-Json -Depth 5
