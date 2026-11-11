# Compila o mod e instala na pasta de mods locais do Stationeers.
# Uso:  powershell -ExecutionPolicy Bypass -File deploy.ps1 [-GameDir "X:\...\Stationeers"]
param(
    [string]$GameDir = "G:\SteamLibrary\steamapps\common\Stationeers",
    [string]$ModsDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) "My Games\Stationeers\mods")
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "ProtectedAreas\ProtectedAreas.csproj"

dotnet build $proj -c Release --nologo -v q "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "Falha na compilacao." }

$target = Join-Path $ModsDir "ProtectedAreas"
New-Item -ItemType Directory -Force (Join-Path $target "About") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $target "GameData") | Out-Null
$aboutSource = Join-Path $root "ProtectedAreas\ModPackage\About\About.xml"
$aboutTarget = Join-Path $target "About\About.xml"
# Depois de publicar na Workshop, o jogo grava o WorkshopHandle no About.xml instalado.
# Traz esse numero de volta para o projeto, para a proxima publicacao atualizar o mesmo item.
if (Test-Path $aboutTarget) {
    $installed = [IO.File]::ReadAllText($aboutTarget)
    if ($installed -match '<WorkshopHandle>(\d+)</WorkshopHandle>' -and $Matches[1] -ne '0') {
        $handle = $Matches[1]
        $text = [IO.File]::ReadAllText($aboutSource)
        if ($text -notmatch "<WorkshopHandle>$handle</WorkshopHandle>") {
            $text = $text -replace '<WorkshopHandle>\d+</WorkshopHandle>', "<WorkshopHandle>$handle</WorkshopHandle>"
            [IO.File]::WriteAllText($aboutSource, $text, (New-Object System.Text.UTF8Encoding($false)))
            Write-Output "WorkshopHandle $handle copiado para o projeto."
        }
    }
}
Copy-Item $aboutSource $aboutTarget -Force
Copy-Item (Join-Path $root "ProtectedAreas\ModPackage\About\thumb.png") (Join-Path $target "About\thumb.png") -Force
Copy-Item (Join-Path $root "ProtectedAreas\ModPackage\About\Preview.png") (Join-Path $target "About\Preview.png") -Force
Copy-Item (Join-Path $root "ProtectedAreas\bin\Release\ProtectedAreas.dll") (Join-Path $target "ProtectedAreas.dll") -Force
$keep = Join-Path $target "GameData\.keep"
if (-not (Test-Path $keep)) { New-Item -ItemType File $keep | Out-Null }

Write-Output "Mod instalado em: $target"
