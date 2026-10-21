# (arquivo so com caracteres ASCII: o PowerShell do Windows le .ps1 sem BOM como ANSI e acento/travessao quebram o script)
# Publica o Controle Tecnico na pasta da rede: um unico ControleTecnico.exe com o icone da Howden
# (nao precisa ter o .NET instalado nos computadores de quem usa).
#   Uso:   powershell -ExecutionPolicy Bypass -File .\publicar.ps1
#          powershell -ExecutionPolicy Bypass -File .\publicar.ps1 -Destino "\\outro\caminho"
# Depois de cada "git pull" com novidades, rode de novo: quem estiver com o sistema aberto ve o aviso "Nova versao publicada".
# Quem abre o .exe da rede recebe uma copia desta versao no proprio computador (so na primeira vez de cada versao):
# abre rapido mesmo pela VPN e da para publicar de novo com o sistema aberto nos outros computadores.
param([string]$Destino = '\\BZVCPFIL003\proj_ramires$\Controletec')

$ErrorActionPreference = 'Stop'
$copia = Get-ChildItem $PSScriptRoot -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'ControleTecnico.csproj') } | Select-Object -First 1
if ($copia) { Write-Host "Aviso: ha outra copia do projeto em $($copia.FullName) - ela e ignorada na compilacao (pode mover ou apagar)." -ForegroundColor Yellow }
$versao = Get-Date -Format 'yyyy.MM.dd-HHmm'
try { $c = git -C $PSScriptRoot rev-parse --short HEAD 2>$null; if ($c) { $versao += "-$c" } } catch { }
$tmp = Join-Path $env:TEMP 'ControleTecnico-publicar'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }

Write-Host "Compilando a versao $versao (Windows 64 bits, arquivo unico) ..."
dotnet publish "$PSScriptRoot\ControleTecnico.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:OutputType=WinExe -o $tmp --nologo
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }
Remove-Item (Join-Path $tmp 'web.config') -ErrorAction SilentlyContinue        # so serve para IIS
Set-Content -Path (Join-Path $tmp 'versao.txt') -Value $versao -Encoding UTF8

# atalho para cada pessoa criar na propria Area de Trabalho (com o icone da Howden)
@'
@echo off
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\Controle Tecnico.lnk'); $s.TargetPath='%~dp0ControleTecnico.exe'; $s.IconLocation='%~dp0ControleTecnico.exe,0'; $s.WorkingDirectory='%~dp0'; $s.Description='Controle Tecnico - Howden'; $s.Save()"
echo Atalho "Controle Tecnico" criado na Area de Trabalho.
pause
'@ -replace "`r?`n", "`r`n" | Set-Content -Encoding ASCII -NoNewline (Join-Path $tmp 'Criar atalho na Area de Trabalho.cmd')

@"
CONTROLE TECNICO - versao $versao

Para abrir: de dois cliques em ControleTecnico.exe (ou no atalho da Area de Trabalho).
Para criar o atalho: de dois cliques em "Criar atalho na Area de Trabalho.cmd" (uma vez so).

- Na primeira vez de cada versao, o programa e copiado para o seu computador (pode levar 1 a 2 minutos pela VPN).
- Abre o navegador com a tela de carregamento (logo e barra de progresso) e entra sozinho no sistema (http://localhost:5090).
- Nao abre janela preta: o servidor roda escondido e FECHA SOZINHO quando voce fecha o sistema no navegador.
- Os dados ficam em \\BZVCPFIL003\proj_ramires$\DB\tec (a VPN precisa estar conectada fora da empresa).
  Sem VPN, a tela de carregamento avisa e tenta de novo a cada 20 s.
- Quando sair versao nova, aparece o aviso "Nova versao publicada": feche o sistema no navegador e abra de novo pelo atalho.
- Registro de funcionamento (para suporte): %LOCALAPPDATA%\ControleTecnico\logs
"@ | Set-Content -Encoding UTF8 (Join-Path $tmp 'LEIA-ME.txt')

Write-Host "Copiando para $Destino ..."
if (-not (Test-Path $Destino)) { New-Item -ItemType Directory -Path $Destino -Force | Out-Null }
# espelha a pasta (remove arquivos de versoes antigas); tenta de novo se alguem estiver abrindo o programa neste instante
robocopy $tmp $Destino /MIR /R:10 /W:3 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar para $Destino (robocopy $LASTEXITCODE). Confira o acesso a pasta e a VPN." }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Publicado: $Destino\ControleTecnico.exe (versao $versao)." -ForegroundColor Green
Write-Host "Cada pessoa pode criar o atalho com 'Criar atalho na Area de Trabalho.cmd' na mesma pasta."
