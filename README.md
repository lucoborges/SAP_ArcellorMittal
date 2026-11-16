# SAP ArcelorMittal — robô de cotações

Robô em .NET 8 + Playwright que acompanha o portal SRM da ArcelorMittal e avisa por e-mail a cada
nova solicitação de cotação publicada.

Para cada conta configurada, o robô:

1. entra no portal e abre a lista de cotações publicadas;
2. para cada cotação ainda não processada, baixa o PDF de "Visualização" e lê os dados (comprador, prazo, itens);
3. grava o evento num banco JSON (controle do que já foi processado);
4. envia um e-mail com o resumo da cotação e o PDF em anexo.

Por padrão ele roda 24/7: verifica todas as contas de hora em hora, com as janelas do navegador fora da
tela, reaproveitando a sessão entre as verificações.

## Configuração

1. Copie `SAP_ArcellorMittal/config.exemplo.json` para `SAP_ArcellorMittal/config.local.json` e preencha
   (usuários do portal, pastas e e-mails). Esse arquivo não vai para o Git.
2. Defina as senhas em variáveis de ambiente (PowerShell) e reabra o terminal:

   ```powershell
   setx ARCELOR_SENHA "senha-da-conta-1"
   setx ARCELOR_SENHA_2 "senha-da-conta-2"
   setx ARCELOR_SMTP_SENHA "senha-do-remetente"
   ```

   Sem as senhas das contas, o robô abre a janela e espera o login manual.
3. Instale o navegador do Playwright uma vez, depois de compilar:

   ```powershell
   dotnet build
   pwsh SAP_ArcellorMittal/bin/Debug/net8.0/playwright.ps1 install chromium
   ```

Qualquer valor do `config.local.json` pode ser trocado por variável de ambiente: `ARCELOR_USUARIO`,
`ARCELOR_USUARIO_2`, `ARCELOR_PASTA_BANCO`, `ARCELOR_PASTA_SAIDA`, `ARCELOR_PLANILHA_VENDEDOR`,
`ARCELOR_EMAIL_REMETENTE`, `ARCELOR_EMAIL_DESTINO`, `ARCELOR_SMTP_HOST`.

## Uso

```powershell
dotnet run --project SAP_ArcellorMittal
```

| Opção | Efeito |
|---|---|
| *(nenhuma)* | 24/7, de hora em hora, em segundo plano (Ctrl+C para parar) |
| `--intervalo-min N` | intervalo entre as verificações (padrão 60) |
| `--uma-vez` | verifica uma única vez e encerra |
| `--janela-visivel` | mantém as janelas do navegador na tela |
| `--login-manual` | espera a pessoa digitar usuário e senha na janela |
| `--conta USUARIO` | processa só essa conta |
| `--sem-email` | não envia e-mails (ficam pendentes no banco) |
| `--max-eventos N` | no máximo N cotações por conta (testes) |
| `--detalhado` | mostra os detalhes técnicos na tela |

## Configuração opcional (endereços, portas e logs)

Nada disso fica fixo no código. Cada item pode ir no `config.local.json` ou em variável de ambiente
(a variável tem prioridade):

| `config.local.json` | Variável | Padrão |
|---|---|---|
| `UrlPortal` / `UrlLogoff` | `ARCELOR_URL_PORTAL` / `ARCELOR_URL_LOGOFF` | portal SRM da ArcelorMittal |
| `NavegadorExecutavel` | `ARCELOR_NAVEGADOR` | Chromium do Playwright |
| `IntervaloMinutos` | `ARCELOR_INTERVALO_MIN` | 60 |
| `PastaLogs` | `ARCELOR_PASTA_LOGS` | `logs\` ao lado do executável |
| `LogDiasGuardados` / `LogTamanhoMaxMb` | `ARCELOR_LOG_DIAS` / `ARCELOR_LOG_MAX_MB` | 30 dias / 10 MB |
| `SaudeEndereco` / `SaudePorta` | `ARCELOR_SAUDE_ENDERECO` / `ARCELOR_SAUDE_PORTA` | `127.0.0.1` / `5080` (`-1` desliga) |
| — | `ARCELOR_SAUDE_TOKEN` | sem token |

O `config.local.json` é procurado em: caminho da variável `ARCELOR_CONFIG` → pasta do executável →
pasta acima do executável (em produção ele fica fora da pasta que o deploy substitui).

## Logs

`robo_AAAAMMDD.log` na pasta de logs: um arquivo por dia, dividido em `robo_AAAAMMDD.N.log` ao passar do
tamanho máximo; arquivos mais antigos que o limite de dias são apagados sozinhos. Senhas (portal, SMTP,
token) nunca são gravadas — se aparecerem numa mensagem de erro, saem como `***`.

## Rota de saúde

No modo 24/7 o robô responde em `GET http://127.0.0.1:5080/health` (ou `/saude`):

- **200** `"status":"ok"` — concluiu um ciclo há menos de 2 intervalos + 30 min (ou acabou de subir);
- **503** `"status":"falha"` — parou de concluir ciclos, ou teve 3 ciclos seguidos com falha grave
  (portal fora do ar, login recusado, lista de cotações indisponível). O campo `motivo` explica.

A resposta traz versão, commit, número do ciclo e horários; não traz usuários nem dados de cotação.

**Autenticação:** por padrão a rota só escuta no próprio servidor. Para consultá-la de outra máquina,
defina `ARCELOR_SAUDE_TOKEN` e o endereço (`SaudeEndereco`, ex.: `0.0.0.0`); a partir daí toda chamada
precisa do cabeçalho `X-Api-Key: <token>` (ou `Authorization: Bearer <token>`). Sem token o robô se
recusa a escutar fora de `127.0.0.1`.

## Parar com calma

`Ctrl+C` no console, ou criar o arquivo `parar.sinal` na pasta de logs: o robô termina a cotação em
andamento, faz logoff no SAP e sai. Matar o processo à força deixa a sessão SAP presa (o robô se
recupera na partida seguinte, mas demora mais).

## Produção (servidor)

Estrutura em `C:\Servicos\SAP_ArcellorMittal\`:

| Pasta | Conteúdo |
|---|---|
| `app\` | versão em execução (publicação Release) |
| `backups\` | versões anteriores (as 5 últimas) |
| `logs\` | logs e o arquivo `parar.sinal` |
| `src\` | cópia do repositório usada pelo deploy |
| `config.local.json` | configuração da instalação (opcional; hoje as variáveis `ARCELOR_*` do usuário bastam) |

O robô roda pela tarefa agendada **RoboArcelorMittal**: sobe no boot, não depende de sessão RDP aberta e
reinicia sozinha se cair. Criar a tarefa (uma vez, como administrador; no modo padrão, S4U, nenhuma senha
é pedida nem guardada — use `-ComSenha` só se o robô precisar de pastas em outra máquina):

```powershell
powershell -File deploy\instalar-tarefa.ps1
```

A tarefa roda como o usuário que tem as variáveis `ARCELOR_*` (senhas). Sem área de trabalho não há login
manual: as senhas das contas precisam estar nas variáveis de ambiente.

### Deploy

Todo push na `main` dispara `.github/workflows/deploy.yml` no runner do próprio servidor
(etiqueta `arcelor`), que chama `deploy\deploy.ps1`:

1. compila e roda os testes em Release — **com o robô ainda no ar**; se falhar, nada muda;
2. pede a parada com calma (até 10 min; depois disso, à força);
3. move `app\` para `backups\` e coloca a versão nova;
4. inicia a tarefa e espera a rota de saúde responder com o commit novo;
5. se não responder em 2 min, restaura o backup, inicia a versão anterior e o workflow termina em erro.

Deploy manual (no servidor, como administrador):

```powershell
powershell -File deploy\deploy.ps1 -Origem C:\caminho\do\repositorio
```

Voltar para uma versão anterior manualmente: parar o robô, trocar `app\` pela pasta desejada de
`backups\` e iniciar a tarefa.

## Testes

```powershell
dotnet test
```

Cobrem o leitor de PDF, o banco de eventos, a captura dos dados do portal, a rota de saúde
(respostas 200/503/401/404), a rotação e o mascaramento do log e a leitura da configuração.
