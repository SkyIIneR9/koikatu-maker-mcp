param([string]$GameRoot = $env:KK_GAME_ROOT, [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $GameRoot) { $GameRoot = [IO.Path]::GetFullPath((Join-Path $root '..\..')) }
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'Koikatu_Data\Managed\Assembly-CSharp.dll'))) {
    throw 'Specify -GameRoot pointing to your Koikatu installation, or set KK_GAME_ROOT.'
}
$managed = Join-Path $GameRoot 'Koikatu_Data\Managed'
$core = Join-Path $GameRoot 'BepInEx\core'
$plugins = Join-Path $GameRoot 'BepInEx\plugins'
$out = Join-Path $root 'build'
if ($Install -and (Get-Process -Name Koikatu -ErrorAction SilentlyContinue)) {
    throw 'Close Koikatu before installing; loaded plugin DLLs are locked. Building without -Install is safe.'
}
New-Item -ItemType Directory -Force $out | Out-Null
# Unity 5.6 has stripped framework libraries; the regular net35 Newtonsoft build
# references missing ComponentModel types. Use the Unity-specific build.
# Give it a private identity to avoid translator/mod conflicts.
Add-Type -LiteralPath (Join-Path $core 'Mono.Cecil.dll')
$jsonPath = Join-Path $root 'lib\json-unity\lib\net35\Newtonsoft.Json.dll'
if (-not (Test-Path -LiteralPath $jsonPath)) {
    $lib = Join-Path $root 'lib'
    New-Item -ItemType Directory -Force $lib | Out-Null
    $archive = Join-Path $lib 'json-unity.zip'
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Json.Net.Unity3D/9.0.1' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '5D142B5BC076D350B8DF3C9208543FECFC3A8DBEE13E330EF6DC5B9809147BF8') {
        throw 'JSON dependency archive hash mismatch'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $lib 'json-unity') -Force
}
$json = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($jsonPath)
$json.Name.Name = 'KKMakerBridge.Json'
$json.Name.HasPublicKey = $false
$json.Name.PublicKey = [byte[]]@()
$json.MainModule.Attributes = $json.MainModule.Attributes -band (-bnot [Mono.Cecil.ModuleAttributes]::StrongNameSigned)
$json.Write((Join-Path $out 'KKMakerBridge.Json.dll'))
$json.Dispose()
$refs = @("$managed\mscorlib.dll", "$managed\System.dll", "$managed\System.Core.dll", "$managed\UnityEngine.dll", "$managed\UnityEngine.UI.dll", "$managed\Assembly-CSharp.dll", "$managed\Assembly-CSharp-firstpass.dll", "$core\BepInEx.dll", "$plugins\KKAPI.dll", "$plugins\KK_BepisPlugins\Screencap.dll", "$out\KKMakerBridge.Json.dll")
$argsBuild = @('/nologo', '/noconfig', '/target:library', '/nostdlib+', '/optimize+', '/langversion:5', "/out:$out\KK_MakerBridge.dll")
$argsBuild += $refs | ForEach-Object { "/reference:$_" }
$argsBuild += Get-ChildItem (Join-Path $root 'plugin') -Filter '*.cs' | ForEach-Object { $_.FullName }
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" @argsBuild
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
if ($Install) {
    $dest = Join-Path $plugins 'KKMakerBridge'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item -LiteralPath "$out\KK_MakerBridge.dll", "$out\KKMakerBridge.Json.dll" -Destination $dest
    Write-Output "Installed in $dest. Restart Koikatu to load the plugin."
}
