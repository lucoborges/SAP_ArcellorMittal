# ============================================================================
# Deploy do robô ArcelorMittal no servidor de produção.
# Compatível com Windows PowerShell 4 (Windows Server 2012 R2).
#
# Ordem (o robô só para depois que a versão nova compilou e passou nos testes):
#   1. compila e testa em Release, publica numa pasta temporária
#   2. pede a parada com calma (arquivo de sinal -> logoff no SAP); força só se não parar no prazo
#   3. guarda a pasta atual em backups\ e coloca a nova no lugar
#   4. inicia a tarefa agendada e confere a rota de saúde (processo no ar + commit esperado)
#   5. se não voltar: restaura o backup, inicia de novo e termina com erro
#
# Uso (normalmente chamado pelo workflow .github/workflows/deploy.yml):
#   powershell -File deploy\deploy.ps1 -Origem C:\Services\SAP_ArcellorMittal\src -Commit <sha>
# ============================================================================
param(
    [string]$Origem = (Split-Path $PSScriptRoot -Parent),
    [string]$Commit = '',
    [string]$Raiz = 'C:\Services\SAP_ArcellorMittal',
    [string]$NomeTarefa = 'RoboArcelorMittal',
    [string]$EnderecoSaude = '127.0.0.1',
    [int]$PortaSaude = 5080,
    [int]$EsperaParadaSeg = 600,
    [int]$EsperaSaudeSeg = 120,
    [int]$BackupsGuardados = 5,
    [switch]$SemTestes
)

# O PowerShell 4 do servidor às vezes falha ao carregar sozinho os módulos básicos
Import-Module Microsoft.PowerShell.Management, Microsoft.PowerShell.Utility, ScheduledTasks -ErrorAction SilentlyContinue
$ErrorActionPreference = 'Stop'
$nomeProcesso = 'SAP_ArcellorMittal'
$app      = Join-Path $Raiz 'app'
$staging  = Join-Path $Raiz 'staging'
$backups  = Join-Path $Raiz 'backups'
$logs     = Join-Path $Raiz 'logs'
$sinal    = Join-Path $logs 'parar.sinal'
$carimbo  = Get-Date -Format 'yyyyMMdd_HHmmss'
$curto    = if ($Commit.Length -ge 7) { $Commit.Substring(0, 7) } else { 'manual' }

function Passo($texto) { Write-Host ''; Write-Host "== $texto" -ForegroundColor Cyan }

function Executar($descricao, [scriptblock]$comando) {
    & $comando
    if ($LASTEXITCODE -ne 0) { throw "$descricao falhou (código $LASTEXITCODE)" }
}

function ProcessosDoRobo {
    @(Get-Process -Name $nomeProcesso -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) })
}

function PararRobo([int]$esperaSeg) {
    if ((ProcessosDoRobo).Count -eq 0) { Write-Host 'O robô não estava rodando.'; return }
    Write-Host "Pedindo a parada com calma (até $esperaSeg s; ele termina a cotação em andamento e faz logoff no SAP)..."
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    Set-Content -Path $sinal -Value "deploy $carimbo"
    $limite = (Get-Date).AddSeconds($esperaSeg)
    while ((Get-Date) -lt $limite -and (ProcessosDoRobo).Count -gt 0) { Start-Sleep -Seconds 3 }

    if ((ProcessosDoRobo).Count -gt 0) {
        Write-Host 'Não parou no prazo: encerrando à força (o robô se recupera da sessão SAP presa na próxima partida).' -ForegroundColor Yellow
        Stop-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue
        ProcessosDoRobo | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
    Remove-Item $sinal -Force -ErrorAction SilentlyContinue
    # O driver do Playwright (node.exe dentro da pasta do app) pode segurar arquivos por alguns segundos
    Get-Process -Name node -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($app, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host 'Robô parado.'
}

function MoverPasta($de, $para) {
    for ($i = 1; $i -le 10; $i++) {
        try { Move-Item -Path $de -Destination $para -Force; return }
        catch { if ($i -eq 10) { throw }; Start-Sleep -Seconds 3 }
    }
}

# Devolve $true quando a rota de saúde responde 200 e (se informado) com o commit esperado
function AguardarSaude([int]$esperaSeg, [string]$commitEsperado) {
    $url = "http://${EnderecoSaude}:$PortaSaude/health"
    $limite = (Get-Date).AddSeconds($esperaSeg)
    $ultimo = 'sem resposta'
    while ((Get-Date) -lt $limite) {
        Start-Sleep -Seconds 5
        if ((ProcessosDoRobo).Count -eq 0) { $ultimo = 'processo não está rodando'; continue }
        try {
            $cliente = New-Object System.Net.WebClient
            $cliente.Encoding = [Text.Encoding]::UTF8
            if ($env:ARCELOR_SAUDE_TOKEN) { $cliente.Headers.Add('X-Api-Key', $env:ARCELOR_SAUDE_TOKEN) }
            $saude = $cliente.DownloadString($url) | ConvertFrom-Json
            if ($commitEsperado -and -not ([string]$saude.commit).StartsWith($commitEsperado, [StringComparison]::OrdinalIgnoreCase)) {
                $ultimo = "respondeu com o commit '$($saude.commit)', esperado '$commitEsperado'"; continue
            }
            Write-Host "Saúde OK: status=$($saude.status) ($($saude.motivo)), versão $($saude.versao), commit $($saude.commit), $($saude.configuracao)." -ForegroundColor Green
            return $true
        } catch { $ultimo = $_.Exception.Message }
    }
    Write-Host "A rota de saúde $url não confirmou em $esperaSeg s: $ultimo" -ForegroundColor Red
    return $false
}

# ---------------------------------------------------------------------------
if (-not (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue)) {
    throw "A tarefa agendada '$NomeTarefa' não existe. Rode deploy\instalar-tarefa.ps1 uma vez antes do primeiro deploy."
}
New-Item -ItemType Directory -Force -Path $Raiz, $backups, $logs | Out-Null

Passo "1/5 Compilando $curto em Release (o robô continua rodando)"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
if (-not $SemTestes) {
    # Os testes que abrem navegador usam o mesmo chrome.exe configurado para o robô
    $config = Join-Path $Raiz 'config.local.json'
    if (-not $env:ARCELOR_NAVEGADOR -and (Test-Path $config)) {
        try { $env:ARCELOR_NAVEGADOR = (Get-Content $config -Raw -Encoding UTF8 | ConvertFrom-Json).NavegadorExecutavel } catch { }
    }
    Executar 'dotnet test' { dotnet test (Join-Path $Origem 'SAP_ArcellorMittal.Tests\SAP_ArcellorMittal.Tests.csproj') -c Release --nologo -v q }
}
Executar 'dotnet publish' { dotnet publish (Join-Path $Origem 'SAP_ArcellorMittal\SAP_ArcellorMittal.csproj') -c Release -o $staging --nologo -v q "/p:SourceRevisionId=$Commit" }
if (-not (Test-Path (Join-Path $staging "$nomeProcesso.exe"))) { throw 'A publicação não gerou o executável.' }

Passo '2/5 Parando o robô'
PararRobo $EsperaParadaSeg

Passo '3/5 Backup da versão atual e troca'
$backupAtual = $null
if (Test-Path $app) {
    $backupAtual = Join-Path $backups "app_${carimbo}_antes_de_$curto"
    MoverPasta $app $backupAtual
    Write-Host "Backup: $backupAtual"
}
MoverPasta $staging $app

Passo '4/5 Iniciando e conferindo'
Start-ScheduledTask -TaskName $NomeTarefa
$ok = AguardarSaude $EsperaSaudeSeg $Commit

if (-not $ok) {
    Passo '5/5 FALHOU: desfazendo'
    Stop-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue
    ProcessosDoRobo | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    MoverPasta $app (Join-Path $backups "app_${carimbo}_FALHOU_$curto")
    if ($backupAtual) {
        MoverPasta $backupAtual $app
        Start-ScheduledTask -TaskName $NomeTarefa
        if (AguardarSaude $EsperaSaudeSeg '') { Write-Host 'Versão anterior restaurada e rodando.' -ForegroundColor Yellow }
        else { Write-Host 'ATENÇÃO: a versão anterior foi restaurada mas NÃO confirmou a saúde. Verifique o servidor.' -ForegroundColor Red }
    } else {
        Write-Host 'Não havia versão anterior para restaurar.' -ForegroundColor Red
    }
    exit 1
}

Passo '5/5 Limpando backups antigos'
Get-ChildItem $backups -Directory | Sort-Object Name -Descending | Select-Object -Skip $BackupsGuardados |
    ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "Removido: $($_.Name)" }

Write-Host ''
Write-Host "Deploy de $curto concluído." -ForegroundColor Green
