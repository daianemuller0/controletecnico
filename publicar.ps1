# Publica o Controle Técnico compilado neste computador e cria um atalho "Controle Técnico" na Área de Trabalho.
# Assim o sistema abre sem compilar a cada vez (o "dotnet run" recompila e demora).
#   Uso:   powershell -ExecutionPolicy Bypass -File .\publicar.ps1
#   Depois de cada "git pull", rode de novo para atualizar.
param([string]$Destino = "$env:LOCALAPPDATA\ControleTecnico\app")

$ErrorActionPreference = 'Stop'
Get-Process ControleTecnico -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Publicando em $Destino ..."
dotnet publish "$PSScriptRoot\ControleTecnico.csproj" -c Release -o $Destino --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar." }

$cmd = Join-Path $Destino "Iniciar Controle Tecnico.cmd"
@"
@echo off
cd /d "$Destino"
start "" /min ControleTecnico.exe
timeout /t 3 /nobreak >nul
start "" http://localhost:5090
"@ | Set-Content -Encoding ASCII $cmd

$atalho = Join-Path ([Environment]::GetFolderPath('Desktop')) "Controle Tecnico.lnk"
$ws = New-Object -ComObject WScript.Shell
$l = $ws.CreateShortcut($atalho); $l.TargetPath = $cmd; $l.WorkingDirectory = $Destino; $l.WindowStyle = 7; $l.Save()
Write-Host "Pronto. Use o atalho 'Controle Tecnico' na Área de Trabalho."
