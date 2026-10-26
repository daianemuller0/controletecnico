# Diagnostico: por que o "dotnet run" abre o ambiente de demonstracao?
#   Uso:      powershell -ExecutionPolicy Bypass -File .\diagnostico.ps1
#   Corrigir: powershell -ExecutionPolicy Bypass -File .\diagnostico.ps1 -Corrigir
# (arquivo so com caracteres ASCII, para o PowerShell do Windows ler certo)
param([switch]$Corrigir)

$nomes = @('Data__Folder', 'Seed__Demo', 'Seed__SenhaPadrao', 'Kestrel__Endpoints__Http__Url', 'ASPNETCORE_ENVIRONMENT', 'Data__UsarEspelho', 'Data__EspelhoLocal')
$achou = $false
Write-Host ""
Write-Host "1) Configuracoes que mudam a base de dados ou a porta" -ForegroundColor Cyan
foreach ($n in $nomes) {
    foreach ($onde in 'Process', 'User', 'Machine') {
        $v = [Environment]::GetEnvironmentVariable($n, $onde)
        if ($v) {
            $achou = $true
            $rot = @{ Process = 'nesta janela'; User = 'gravada no seu usuario'; Machine = 'gravada no computador' }[$onde]
            Write-Host ("   {0} = {1}   ({2})" -f $n, $v, $rot) -ForegroundColor Yellow
        }
    }
}
if (-not $achou) { Write-Host "   nenhuma (ok: usa a base da rede do appsettings.json)" -ForegroundColor Green }

Write-Host ""
Write-Host "2) Programa usando a porta 5090" -ForegroundColor Cyan
$donos = @(Get-NetTCPConnection -LocalPort 5090 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
if ($donos.Count -eq 0) { Write-Host "   nenhum (porta livre)" -ForegroundColor Green }
foreach ($id in $donos) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    Write-Host ("   pid {0}  {1}  {2}" -f $id, $p.ProcessName, $p.Path) -ForegroundColor Yellow
}

Write-Host ""
Write-Host "3) Base configurada no appsettings.json" -ForegroundColor Cyan
try { $cfg = Get-Content (Join-Path $PSScriptRoot 'appsettings.json') -Raw | ConvertFrom-Json; Write-Host ("   {0}  (Seed.Demo = {1})" -f $cfg.Data.Folder, $cfg.Seed.Demo) } catch { Write-Host "   nao foi possivel ler" }

if ($Corrigir) {
    Write-Host ""
    Write-Host "Corrigindo ..." -ForegroundColor Cyan
    foreach ($n in $nomes) {
        foreach ($onde in 'Process', 'User') { if ([Environment]::GetEnvironmentVariable($n, $onde)) { [Environment]::SetEnvironmentVariable($n, $null, $onde); Write-Host "   removida $n ($onde)" } }
        if ([Environment]::GetEnvironmentVariable($n, 'Machine')) { Write-Host "   $n esta gravada no COMPUTADOR: peca para a TI remover (ou rode como administrador)." -ForegroundColor Yellow }
    }
    foreach ($id in $donos) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -in @('ControleTecnico', 'dotnet')) { Stop-Process -Id $id -Force; Write-Host "   encerrado pid $id ($($p.ProcessName)) que ocupava a porta 5090" }
    }
    Write-Host ""
    Write-Host "Pronto. FECHE esta janela do PowerShell (e o VS Code, se usar o terminal dele), abra de novo e rode: dotnet run" -ForegroundColor Green
} elseif ($achou -or $donos.Count -gt 0) {
    Write-Host ""
    Write-Host "Para corrigir: powershell -ExecutionPolicy Bypass -File .\diagnostico.ps1 -Corrigir" -ForegroundColor Green
}
Write-Host ""
Write-Host "Depois do dotnet run, a linha 'Ambiente:' mostra se abriu com DADOS REAIS ou DEMONSTRACAO." 
