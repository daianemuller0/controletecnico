# Publica o Controle Técnico na pasta da rede: um único ControleTecnico.exe com o ícone da Howden
# (não precisa ter o .NET instalado nos computadores de quem usa).
#   Uso:   powershell -ExecutionPolicy Bypass -File .\publicar.ps1
#          powershell -ExecutionPolicy Bypass -File .\publicar.ps1 -Destino "\\outro\caminho"
# Depois de cada "git pull" com novidades, rode de novo: quem estiver com o sistema aberto vê o aviso "Nova versão publicada".
# Quem abre o .exe da rede recebe uma cópia desta versão no próprio computador (só na primeira vez de cada versão):
# abre rápido mesmo pela VPN e dá para publicar de novo com o sistema aberto nos outros computadores.
param([string]$Destino = '\\BZVCPFIL003\proj_ramires$\Controletec')

$ErrorActionPreference = 'Stop'
$copia = Get-ChildItem $PSScriptRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ControleTecnico.csproj') } | Select-Object -First 1
if ($copia) { Write-Host "Aviso: há outra cópia do projeto em $($copia.FullName) — ela é ignorada na compilação (pode mover ou apagar)." -ForegroundColor Yellow }
$versao = Get-Date -Format 'yyyy.MM.dd-HHmm'
try { $c = git -C $PSScriptRoot rev-parse --short HEAD 2>$null; if ($c) { $versao += "-$c" } } catch { }
$tmp = Join-Path $env:TEMP 'ControleTecnico-publicar'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

Write-Host "Compilando a versão $versao (Windows 64 bits, arquivo único) ..."
dotnet publish "$PSScriptRoot\ControleTecnico.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $tmp --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }
Remove-Item (Join-Path $tmp 'web.config') -ErrorAction SilentlyContinue        # só serve para IIS
Set-Content -Path (Join-Path $tmp 'versao.txt') -Value $versao -Encoding UTF8

# atalho para cada pessoa criar na própria Área de Trabalho (com o ícone da Howden)
@'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\Controle Tecnico.lnk'); $s.TargetPath='%~dp0ControleTecnico.exe'; $s.IconLocation='%~dp0ControleTecnico.exe,0'; $s.WorkingDirectory='%~dp0'; $s.Description='Controle Tecnico - Howden'; $s.Save()"
echo Atalho "Controle Tecnico" criado na Area de Trabalho.
pause
'@ | Set-Content -Encoding ASCII (Join-Path $tmp 'Criar atalho na Area de Trabalho.cmd')

@"
CONTROLE TÉCNICO — versão $versao

Para abrir: dê dois cliques em ControleTecnico.exe (ou no atalho da Área de Trabalho).
Para criar o atalho: dê dois cliques em "Criar atalho na Area de Trabalho.cmd" (uma vez só).

- Na primeira vez de cada versão, o programa é copiado para o seu computador (pode levar 1 a 2 minutos pela VPN).
- Abre uma janela preta (o servidor do sistema) e o navegador em http://localhost:5090.
  Mantenha a janela preta aberta enquanto usar o sistema (pode minimizar); para encerrar, feche-a.
- Os dados ficam em \\BZVCPFIL003\proj_ramires$\DB\tec (a VPN precisa estar conectada fora da empresa).
- Quando sair versão nova, aparece o aviso "Nova versão publicada": feche a janela preta e abra de novo.
"@ | Set-Content -Encoding UTF8 (Join-Path $tmp 'LEIA-ME.txt')

Write-Host "Copiando para $Destino ..."
if (-not (Test-Path $Destino)) { New-Item -ItemType Directory -Path $Destino -Force | Out-Null }
# espelha a pasta (remove arquivos de versões antigas); tenta de novo se alguém estiver abrindo o programa neste instante
robocopy $tmp $Destino /MIR /R:10 /W:3 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar para $Destino (robocopy $LASTEXITCODE). Confira o acesso à pasta e a VPN." }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Publicado: $Destino\ControleTecnico.exe (versão $versao)." -ForegroundColor Green
Write-Host "Cada pessoa pode criar o atalho com 'Criar atalho na Area de Trabalho.cmd' na mesma pasta."
