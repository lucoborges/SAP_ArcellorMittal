# ============================================================================
# Cria a tarefa agendada que mantém o robô no ar SEM depender de sessão RDP:
# sobe no boot do Windows, roda como o usuário indicado "estando ele conectado ou não",
# sem limite de tempo, e reinicia sozinha se o robô cair.
# Compatível com Windows PowerShell 4 (Windows Server 2012 R2). Rodar como administrador, UMA vez.
#
# O usuário precisa ser o mesmo que tem as variáveis de ambiente ARCELOR_* (senhas do portal e do SMTP).
#
# Padrão: modo S4U — o Windows abre a sessão do usuário SEM guardar a senha dele. Nesse modo a tarefa não
# tem credencial de rede; serve aqui porque as pastas do robô ficam no próprio servidor.
# Com -ComSenha a senha do usuário é pedida pelo Windows (schtasks) e guardada pelo Agendador de Tarefas;
# use se o robô precisar acessar pastas de OUTRA máquina.
#
# Uso:
#   powershell -File deploy\instalar-tarefa.ps1                # cria/atualiza a tarefa em S4U (não inicia)
#   powershell -File deploy\instalar-tarefa.ps1 -ComSenha      # idem, guardando a senha do usuário
#   powershell -File deploy\instalar-tarefa.ps1 -Remover       # remove a tarefa
# ============================================================================
param(
    [string]$Raiz = 'C:\Servicos\SAP_ArcellorMittal',
    [string]$NomeTarefa = 'RoboArcelorMittal',
    [string]$Usuario = "$env:COMPUTERNAME\Administrator",
    [switch]$ComSenha,
    [switch]$Remover
)

$ErrorActionPreference = 'Stop'

if ($Remover) {
    Unregister-ScheduledTask -TaskName $NomeTarefa -Confirm:$false
    Write-Host "Tarefa '$NomeTarefa' removida."
    exit 0
}

$app = Join-Path $Raiz 'app'
$tipoLogon = if ($ComSenha) { 'Password' } else { 'S4U' }
New-Item -ItemType Directory -Force -Path $Raiz, (Join-Path $Raiz 'logs'), (Join-Path $Raiz 'backups') | Out-Null

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Robô de cotações do portal SRM ArcelorMittal (24/7). Parada com calma: criar $Raiz\logs\parar.sinal</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger>
      <Enabled>true</Enabled>
      <Delay>PT2M</Delay>
    </BootTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$Usuario</UserId>
      <LogonType>$tipoLogon</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT2M</Interval>
      <Count>30</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$app\SAP_ArcellorMittal.exe</Command>
      <WorkingDirectory>$app</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

Write-Host "Criando a tarefa '$NomeTarefa' para o usuário $Usuario (logon $tipoLogon)."
if ($ComSenha) {
    $arquivoXml = Join-Path $env:TEMP "$NomeTarefa.xml"
    Set-Content -Path $arquivoXml -Value $xml -Encoding Unicode
    Write-Host 'O Windows vai pedir a senha desse usuário agora (ela fica guardada só pelo Agendador de Tarefas):' -ForegroundColor Yellow
    schtasks.exe /Create /TN $NomeTarefa /XML $arquivoXml /RU $Usuario /RP * /F
    $codigo = $LASTEXITCODE
    Remove-Item $arquivoXml -Force -ErrorAction SilentlyContinue
    if ($codigo -ne 0) { throw "schtasks falhou (código $codigo)" }
} else {
    Register-ScheduledTask -TaskName $NomeTarefa -Xml $xml -Force | Out-Null
}

Write-Host ''
Write-Host "Tarefa '$NomeTarefa' criada. Ela NÃO foi iniciada." -ForegroundColor Green
Write-Host "Executável esperado: $app\SAP_ArcellorMittal.exe (o deploy coloca lá)."
Write-Host "Iniciar:  Start-ScheduledTask -TaskName $NomeTarefa"
Write-Host "Parar:    New-Item $Raiz\logs\parar.sinal   (com calma)   |   Stop-ScheduledTask -TaskName $NomeTarefa   (à força)"
