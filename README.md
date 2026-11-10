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

O log completo fica em `logs\robo_AAAAMMDD.log`, ao lado do executável.

## Testes

```powershell
dotnet test
```
