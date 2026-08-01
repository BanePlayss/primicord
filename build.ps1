# Gera o Primicord.exe: um arquivo so, roda sem .NET instalado.
# Uso:  ./build.ps1            (gera na raiz do projeto)
#       ./build.ps1 -Open      (gera e ja abre)
#
# ATENCAO: este arquivo e SO ASCII de proposito. O Windows PowerShell 5.1 le .ps1
# como ANSI quando nao ha BOM, entao acento/travessao viram bytes soltos e o
# parser quebra com "Missing closing '}'" numa linha que nao tem nada de errado.

param([switch]$Open)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# --- 1. empacota o motor de video -------------------------------------------
# O libVLC sao ~100MB de DLLs + plugins que o publish em arquivo unico NAO leva
# junto (so os .lib inuteis vao). Empacotamos uma versao enxuta que entra no exe
# como recurso embutido e se instala sozinha na primeira sessao cinema.
# Plugins fora da lista abaixo sao de servidor de streaming, visualizacao e
# descoberta de rede: nada disso serve pra tocar um arquivo local.
$vlcZip = Join-Path $root "vlc-runtime.zip"

function Find-VlcSource($binRoot) {
    if (-not (Test-Path $binRoot)) { return $null }
    $hits = Get-ChildItem -Path $binRoot -Recurse -Filter "libvlc.dll" -File -ErrorAction SilentlyContinue
    foreach ($h in $hits) { if ($h.Directory.Name -eq "win-x64") { return $h.Directory } }
    return $null
}

$vlcSrc = Find-VlcSource (Join-Path $root "bin")
if (-not $vlcSrc) {
    Write-Host "libvlc.dll ainda nao materializado - compilando uma vez..." -ForegroundColor DarkYellow
    dotnet build "$root\Primicord.csproj" -c Release | Out-Null
    $vlcSrc = Find-VlcSource (Join-Path $root "bin")
}

if ($vlcSrc) {
    $stamp = Join-Path $root "vlc-runtime.stamp"
    $srcTime = (Get-Item (Join-Path $vlcSrc.FullName "libvlc.dll")).LastWriteTimeUtc.Ticks
    $precisa = (-not (Test-Path $vlcZip)) -or (-not (Test-Path $stamp))
    if (-not $precisa) { $precisa = (Get-Content $stamp) -ne $srcTime }

    if ($precisa) {
        Write-Host "Empacotando o motor de video (enxuto)..." -ForegroundColor DarkYellow
        $stage = Join-Path $env:TEMP "primicord-vlc-stage"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item -ItemType Directory $stage | Out-Null

        Copy-Item (Join-Path $vlcSrc.FullName "libvlc.dll")     $stage
        Copy-Item (Join-Path $vlcSrc.FullName "libvlccore.dll") $stage

        $manter = @(
            "access", "audio_filter", "audio_mixer", "audio_output", "codec",
            "d3d9", "d3d11", "demux", "logger", "meta_engine", "misc", "packetizer",
            "spu", "stream_filter", "text_renderer",
            "video_chroma", "video_filter", "video_output", "video_splitter"
        )
        $plugDest = Join-Path $stage "plugins"
        New-Item -ItemType Directory $plugDest | Out-Null
        foreach ($p in $manter) {
            $src = Join-Path $vlcSrc.FullName "plugins\$p"
            if (Test-Path $src) { Copy-Item $src $plugDest -Recurse }
        }

        if (Test-Path $vlcZip) { Remove-Item $vlcZip -Force }
        Compress-Archive -Path "$stage\*" -DestinationPath $vlcZip -CompressionLevel Optimal
        Set-Content $stamp $srcTime
        Remove-Item $stage -Recurse -Force
        $zmb = [math]::Round((Get-Item $vlcZip).Length / 1MB, 1)
        Write-Host "  motor de video: $zmb MB" -ForegroundColor DarkGray
    }
} else {
    Write-Host "AVISO: libvlc nao encontrado - a sessao cinema ficara indisponivel" -ForegroundColor Yellow
}

# --- 2. publica -------------------------------------------------------------
Write-Host "Publicando Primicord (self-contained, single-file)..." -ForegroundColor DarkYellow
dotnet publish "$root\Primicord.csproj" -c Release -o "$root\publish" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "publish falhou" }

Copy-Item "$root\publish\Primicord.exe" "$root\Primicord.exe" -Force
$mb = [math]::Round((Get-Item "$root\Primicord.exe").Length / 1MB, 1)
Write-Host "OK -> $root\Primicord.exe ($mb MB)" -ForegroundColor Green

if ($Open) { Start-Process "$root\Primicord.exe" }
