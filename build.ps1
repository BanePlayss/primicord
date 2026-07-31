# Gera o Primicord.exe: um arquivo so, roda sem .NET instalado.
# Uso:  ./build.ps1            (gera na raiz do projeto)
#       ./build.ps1 -Open      (gera e ja abre)

param([switch]$Open)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "Publicando Primicord (self-contained, single-file)..." -ForegroundColor DarkYellow
dotnet publish "$root\Primicord.csproj" -c Release -o "$root\publish" `
  -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "publish falhou" }

Copy-Item "$root\publish\Primicord.exe" "$root\Primicord.exe" -Force
$mb = [math]::Round((Get-Item "$root\Primicord.exe").Length / 1MB, 1)
Write-Host "OK -> $root\Primicord.exe ($mb MB)" -ForegroundColor Green

if ($Open) { Start-Process "$root\Primicord.exe" }
