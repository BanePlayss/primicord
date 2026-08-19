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

# O libvlc vem do CACHE DO NUGET, nao do bin. O csproj desliga a copia pro output
# de proposito (Vlc*Enabled=false): as tres arquiteturas iam parar dentro do exe e
# custavam ~135MB de peso que o app nunca usa. O que ele usa e este zip curado.
function Find-VlcSource {
    $roots = @()
    if ($env:NUGET_PACKAGES) { $roots += $env:NUGET_PACKAGES }
    $roots += (Join-Path $env:USERPROFILE ".nuget\packages")

    foreach ($r in $roots) {
        $pkg = Join-Path $r "videolan.libvlc.windows"
        if (-not (Test-Path $pkg)) { continue }
        $vers = Get-ChildItem $pkg -Directory -ErrorAction SilentlyContinue |
                Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending
        foreach ($v in $vers) {
            $x64 = Join-Path $v.FullName "build\x64"
            if (Test-Path (Join-Path $x64 "libvlc.dll")) { return (Get-Item $x64) }
        }
    }
    return $null
}

$vlcSrc = Find-VlcSource
if (-not $vlcSrc) {
    Write-Host "libvlc nao esta no cache do NuGet - restaurando uma vez..." -ForegroundColor DarkYellow
    dotnet restore "$root\Primicord.csproj" | Out-Null
    $vlcSrc = Find-VlcSource
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
# PASTA, e nao arquivo unico comprimido. E o que torna a atualizacao diferencial
# possivel: o Velopack faz patch binario por ARQUIVO, entao mudanca so de codigo
# baixa so o Primicord.dll. Num arquivo unico comprimido, uma linha de codigo
# muda os bytes do arquivo inteiro e o delta viraria os 100MB de novo.
Write-Host "Publicando Primicord (self-contained, pasta)..." -ForegroundColor DarkYellow
$pub = Join-Path $root "publish"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$root\Primicord.csproj" -c Release -o $pub -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) { throw "publish falhou" }

# O zip do VLC vai como ARQUIVO ao lado do exe (ver VlcRuntime.cs). Embutido no
# assembly ele entraria no arquivo que muda a cada versao e estragaria o delta.
if (Test-Path $vlcZip) { Copy-Item $vlcZip $pub -Force }

# O coordenador local vai junto no pacote. Ele e um processo separado porque o PC
# servidor precisa continuar atendendo mesmo quando a janela do Primicord fecha.
Write-Host "Publicando mini servidor..." -ForegroundColor DarkYellow
$serverPub = Join-Path $root "server\bin\Release\publish"
if (Test-Path $serverPub) { Remove-Item $serverPub -Recurse -Force }
dotnet publish "$root\server\Primicord.Server.csproj" -c Release -o $serverPub -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) { throw "publish do mini servidor falhou" }
Copy-Item (Join-Path $serverPub "Primicord.Server.exe") $pub -Force

# --- 3. empacota o instalador ----------------------------------------------
$ver = ([xml](Get-Content "$root\Primicord.csproj")).Project.PropertyGroup.Version
$ver = ($ver | Where-Object { $_ }) -as [string]
if (-not $ver) { throw "nao achei a <Version> no csproj" }

$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Host "instalando a ferramenta vpk..." -ForegroundColor DarkYellow
    dotnet tool install -g vpk | Out-Null
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
}

$rel = Join-Path $root "Releases"
Write-Host "Empacotando instalador $ver..." -ForegroundColor DarkYellow
# Reconstruir a mesma versao precisa ser seguro durante desenvolvimento. Remove
# somente os artefatos gerados DESTA versao e os indices (o vpk os recria lendo
# todos os pacotes antigos que continuam na pasta).
foreach ($name in @(
    "Primicord-$ver-full.nupkg", "Primicord-$ver-delta.nupkg",
    "Primicord-win-Setup.exe", "Primicord-win-Portable.zip",
    "assets.win.json", "releases.win.json", "RELEASES"
)) {
    $generated = Join-Path $rel $name
    if (Test-Path $generated) { Remove-Item -LiteralPath $generated -Force }
}
vpk pack --packId Primicord --packVersion $ver --packDir $pub --mainExe Primicord.exe `
         --packTitle PRIMICORD --packAuthors Primitivao --icon "$root\Primicord.ico" `
         --outputDir $rel
if ($LASTEXITCODE -ne 0) { throw "vpk pack falhou" }

# O Setup do Velopack instala/atualiza o Primicord. Um bootstrap pequeno fica por
# fora dele para instalar o Tailscale antes, sem mudar o atualizador diferencial.
$velopackSetup = Join-Path $rel "Primicord-win-Setup.exe"
if (Test-Path $velopackSetup) {
    Write-Host "Integrando Tailscale ao instalador..." -ForegroundColor DarkYellow
    $setupCache = Join-Path $env:TEMP "primicord-velopack-$ver.exe"
    Copy-Item $velopackSetup $setupCache -Force
    $installerPub = Join-Path $root "installer\bin\Release\publish"
    if (Test-Path $installerPub) { Remove-Item $installerPub -Recurse -Force }
    dotnet publish "$root\installer\Primicord.Installer.csproj" -c Release -o $installerPub `
        -r win-x64 --self-contained true "/p:PrimicordSetupPath=$setupCache"
    if ($LASTEXITCODE -ne 0) { throw "bootstrap do instalador falhou" }
    Copy-Item (Join-Path $installerPub "Primicord.Setup.exe") $velopackSetup -Force
    $verify = Start-Process -FilePath $velopackSetup -ArgumentList "--verify" -Wait -PassThru -WindowStyle Hidden
    if ($verify.ExitCode -ne 0) { throw "bootstrap nao contem o Setup interno (codigo $($verify.ExitCode))" }
    Remove-Item $setupCache -Force
}

Get-ChildItem $rel -File | Where-Object { $_.Name -match "$([regex]::Escape($ver))|Setup" } |
    Sort-Object Length -Descending | ForEach-Object {
        "{0,8:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name
    } | Write-Host

$setup = Join-Path $rel "Primicord-win-Setup.exe"
if (Test-Path $setup) {
    Write-Host "OK -> $setup" -ForegroundColor Green
    if ($Open) { Start-Process $setup }
} else {
    Write-Host "AVISO: nao achei o Setup em $rel" -ForegroundColor Yellow
}
