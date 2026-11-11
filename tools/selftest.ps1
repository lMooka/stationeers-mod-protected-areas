# Compila o mod com o autoteste, abre o Stationeers em um mundo novo descartavel, espera o resultado
# no log do BepInEx e reinstala a versao normal (sem o codigo de teste).
# Uso:  powershell -ExecutionPolicy Bypass -File tools\selftest.ps1
param(
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Stationeers",
    [int]$TimeoutSeconds = 280
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root "ProtectedAreas\ProtectedAreas.csproj"
$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "My Games\Stationeers"
$modDll = Join-Path $docs "mods\ProtectedAreas\ProtectedAreas.dll"
$log = Join-Path $GameDir "BepInEx\LogOutput.log"
$flag = Join-Path ([IO.Path]::GetTempPath()) "protectedareas_selftest.flag"

if (Get-Process rocketstation -ErrorAction SilentlyContinue) { throw "O Stationeers esta aberto. Feche-o antes de rodar o autoteste." }

dotnet build $proj -c Release --nologo -v q "-p:DefineConstants=SELFTEST" "-p:GameDir=$GameDir" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao do autoteste." }

$savesBefore = (Get-ChildItem (Join-Path $docs "saves") -Directory).Name
Copy-Item (Join-Path $root "ProtectedAreas\bin\Release\ProtectedAreas.dll") $modDll -Force
Set-Content $flag "1"
$before = (Get-Item $log).LastWriteTime
$done = $false
try {
    Start-Process (Join-Path $GameDir "rocketstation.exe") -WorkingDirectory $GameDir
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 6
        if ((Get-Item $log).LastWriteTime -gt $before) {
            $content = Get-Content $log -Raw -ErrorAction SilentlyContinue
            if ($content -match "SELFTEST DONE") { $done = $true; Start-Sleep -Seconds 8; break }
        }
    }
}
finally {
    Remove-Item $flag -Force -ErrorAction SilentlyContinue
}

Get-Content $log | Where-Object { $_ -match "SELFTEST|\[Error|\[Warning.*ProtectedAreas|patches applied|^\s+at " } |
    Where-Object { $_ -notmatch "SELFTEST status" }

if (-not $done) {
    Write-Output "AUTOTESTE NAO TERMINOU em $TimeoutSeconds s. Jogo ainda aberto: $([bool](Get-Process rocketstation -ErrorAction SilentlyContinue))"
    return
}

# Reinstala a versao normal.
dotnet build $proj -c Release --nologo -v q "-p:GameDir=$GameDir" | Out-Null
Copy-Item (Join-Path $root "ProtectedAreas\bin\Release\ProtectedAreas.dll") $modDll -Force
$bytes = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($modDll))
$savesNew = @((Get-ChildItem (Join-Path $docs "saves") -Directory).Name | Where-Object { $savesBefore -notcontains $_ })
Write-Output "Versao normal reinstalada. Contem SelfTest: $($bytes.Contains('SelfTest')). Pastas de save novas: [$($savesNew -join ', ')]. Jogo aberto: $([bool](Get-Process rocketstation -ErrorAction SilentlyContinue))"
