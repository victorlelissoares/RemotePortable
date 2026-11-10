param([switch]$SkipDownload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
Set-Location $root
$ffmpeg = Join-Path $root 'ffmpeg.exe'

function Assert-FFmpeg([string]$path) {
    if (-not (Test-Path $path)) { throw "FFmpeg nao encontrado: $path" }
    $filters = (& $path -hide_banner -filters 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $filters -notmatch '\bddagrab\b') { throw 'Esta versao do FFmpeg nao inclui o filtro ddagrab (DXGI).' }
    $encoders = (& $path -hide_banner -encoders 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $encoders -notmatch '\bh264_mf\b') { throw 'Esta versao do FFmpeg nao inclui o encoder h264_mf.' }
}

if (-not (Test-Path $ffmpeg)) {
    if ($SkipDownload) { throw 'FFmpeg ausente e -SkipDownload solicitado.' }
    $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip'
    $tempDir = Join-Path ([IO.Path]::GetTempPath()) ('RemotePortable-ffmpeg-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    try {
        $zip = Join-Path $tempDir 'ffmpeg.zip'
        Write-Host "Baixando FFmpeg de $url ..."
        Invoke-WebRequest -Uri $url -OutFile $zip -MaximumRedirection 5
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.Entries | Where-Object { $_.FullName -match '(^|/)bin/ffmpeg\.exe$' } | Select-Object -First 1
            if ($null -eq $entry) { throw 'ffmpeg.exe nao encontrado no pacote oficial da distribuicao.' }
            $sourceStream = $entry.Open()
            try {
                $targetStream = [IO.File]::Create($ffmpeg)
                try { $sourceStream.CopyTo($targetStream) } finally { $targetStream.Dispose() }
            } finally { $sourceStream.Dispose() }
        } finally { $archive.Dispose() }
        Assert-FFmpeg $ffmpeg
    } catch {
        Remove-Item $ffmpeg -Force -ErrorAction SilentlyContinue
        throw
    } finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Assert-FFmpeg $ffmpeg
Write-Host 'Compilando RemotePortable...'
dotnet publish (Join-Path $root 'RemotePortable.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Falha no dotnet publish.' }
Write-Host 'Compilando Tunnel Windows...'
dotnet publish (Join-Path $root 'relay/RemotePortable.Tunnel/RemotePortable.Tunnel.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { throw 'Falha no publish do Tunnel.' }
Remove-Item (Join-Path $root 'publish/ffmpeg.exe') -Force -ErrorAction SilentlyContinue
Write-Host 'Concluido: publish/RemotePortable.exe (FFmpeg incorporado)'
