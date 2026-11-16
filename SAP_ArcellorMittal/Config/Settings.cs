using System;
using System.Collections.Generic;
using System.IO;

namespace SAP_ArcellorMittal.Config
{
    public static class Settings
    {
        // Nada específico da empresa fica no código (usuários, pastas da rede, e-mails): vem do arquivo
        // config.local.json, ao lado do executável (modelo: config.exemplo.json; o arquivo real não vai para o Git),
        // ou de variáveis de ambiente, que têm prioridade.
        //
        // Senhas ficam SÓ em variáveis de ambiente (PowerShell, uma por conta, na ordem do config):
        //   setx ARCELOR_SENHA "senha-da-conta-1"      setx ARCELOR_SENHA_2 "senha-da-conta-2"   (_3, _4...)
        //   setx ARCELOR_SMTP_SENHA "senha-do-remetente"
        // (feche e reabra o terminal/Visual Studio depois do setx)

        private static readonly ConfigLocal Local = ConfigLocal.Carregar();

        private static string Env(string nome, string padrao)
        {
            var valor = Environment.GetEnvironmentVariable(nome);
            return string.IsNullOrWhiteSpace(valor) ? padrao : valor;
        }

        private static string Obrigatorio(string valor, string chave, string variavel) =>
            !string.IsNullOrWhiteSpace(valor)
                ? valor
                : throw new InvalidOperationException(
                    $"Configuração '{chave}' ausente: preencha o config.local.json (modelo: config.exemplo.json) ou a variável de ambiente {variavel}.");

        /// <summary>
        /// Contas que o robô processa, em ordem. Todas seguem exatamente o mesmo processo.
        /// Usuário: ARCELOR_USUARIO, ARCELOR_USUARIO_2... ou a lista "Contas" do config.local.json.
        /// Senha: ARCELOR_SENHA, ARCELOR_SENHA_2... (pode faltar com --login-manual).
        /// </summary>
        public static List<ContaPortal> Contas
        {
            get
            {
                var contas = new List<ContaPortal>();
                for (int i = 1; i <= Math.Max(Local.Contas.Count, 9); i++)
                {
                    var sufixo = i == 1 ? "" : $"_{i}";
                    var usuario = Env($"ARCELOR_USUARIO{sufixo}", i <= Local.Contas.Count ? Local.Contas[i - 1] : null);
                    if (string.IsNullOrWhiteSpace(usuario)) continue;
                    contas.Add(new ContaPortal(usuario.Trim(), Environment.GetEnvironmentVariable($"ARCELOR_SENHA{sufixo}"), $"ARCELOR_SENHA{sufixo}"));
                }
                return contas;
            }
        }

        private static int EnvInt(string nome, int doConfig, int padrao)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(nome), out var v) && v != 0) return v;
            return doConfig != 0 ? doConfig : padrao;
        }

        public static string UrlPortal => Env("ARCELOR_URL_PORTAL", Local.UrlPortal ?? "https://srm.arcelormittal.com.br/sap/bc/nwbc/");
        /// <summary>Logoff padrão do SAP (ICF): encerra a sessão no servidor.</summary>
        public static string UrlLogoff => Env("ARCELOR_URL_LOGOFF", Local.UrlLogoff ?? "https://srm.arcelormittal.com.br/sap/public/bc/icf/logoff");

        /// <summary>chrome.exe a usar (ex.: o Chrome instalado no servidor). Vazio = Chromium do Playwright.</summary>
        public static string NavegadorExecutavel => Env("ARCELOR_NAVEGADOR", Local.NavegadorExecutavel);

        /// <summary>Minutos entre as verificações (a opção --intervalo-min tem prioridade).</summary>
        public static int IntervaloMinutos => Math.Max(1, EnvInt("ARCELOR_INTERVALO_MIN", Local.IntervaloMinutos, 60));

        // Logs: pasta, dias guardados e tamanho máximo de cada arquivo
        public static string PastaLogs => Env("ARCELOR_PASTA_LOGS", Local.PastaLogs ?? Path.Combine(AppContext.BaseDirectory, "logs"));
        public static int LogDiasGuardados => Math.Max(1, EnvInt("ARCELOR_LOG_DIAS", Local.LogDiasGuardados, 30));
        public static int LogTamanhoMaxMb => Math.Max(1, EnvInt("ARCELOR_LOG_MAX_MB", Local.LogTamanhoMaxMb, 10));

        /// <summary>Criar este arquivo pede ao robô para encerrar com calma (logoff no SAP). Usado pelo deploy.</summary>
        public static string ArquivoParada => Path.Combine(PastaLogs, "parar.sinal");

        // Rota de saúde: GET http://endereço:porta/health. Porta -1 desliga.
        // Fora do próprio servidor (endereço diferente de 127.0.0.1) só funciona com ARCELOR_SAUDE_TOKEN definido.
        public static string SaudeEndereco => Env("ARCELOR_SAUDE_ENDERECO", Local.SaudeEndereco ?? "127.0.0.1");
        public static int SaudePorta => EnvInt("ARCELOR_SAUDE_PORTA", Local.SaudePorta, 5080);
        public static string SaudeToken => Environment.GetEnvironmentVariable("ARCELOR_SAUDE_TOKEN");

        // Planilha do vendedor: só as cotações que ele já finalizou. O robô apenas LÊ (para não reprocessar
        // o que já foi finalizado). Opcional: sem ela, o controle é só pelo banco JSON.
        public static string CaminhoPlanilhaBase => Env("ARCELOR_PLANILHA_VENDEDOR", Local.PlanilhaVendedor);
        public const string NomeColunaSolicitacao = "SOLICITAÇAO";
        public const string NomeAbaBase = ""; // vazio = usa a primeira aba da planilha

        // Banco JSON com os eventos já processados pelo robô (fonte principal de controle).
        public static string PastaBanco => Obrigatorio(Env("ARCELOR_PASTA_BANCO", Local.PastaBanco), "PastaBanco", "ARCELOR_PASTA_BANCO");
        public static string ArquivoBancoEventos => Path.Combine(PastaBanco, "arcelormittal_eventos.json");

        // Pasta de saída: todos os PDFs baixados
        public static string PastaSaida => Obrigatorio(Env("ARCELOR_PASTA_SAIDA", Local.PastaSaida), "PastaSaida", "ARCELOR_PASTA_SAIDA");
        public static string PastaPdfs => PastaSaida;

        // E-mail enviado a cada cotação processada (SMTP 587/STARTTLS).
        // O host deve ser o nome que casa com o certificado do servidor (validação TLS integral).
        public static string EmailRemetente => Obrigatorio(Env("ARCELOR_EMAIL_REMETENTE", Local.EmailRemetente), "EmailRemetente", "ARCELOR_EMAIL_REMETENTE");
        public static string EmailDestino => Obrigatorio(Env("ARCELOR_EMAIL_DESTINO", Local.EmailDestino), "EmailDestino", "ARCELOR_EMAIL_DESTINO");
        public static string SmtpHost => Obrigatorio(Env("ARCELOR_SMTP_HOST", Local.SmtpHost), "SmtpHost", "ARCELOR_SMTP_HOST");
        public static int SmtpPorta => Local.SmtpPorta > 0 ? Local.SmtpPorta : 587;

        /// <summary>
        /// Senha do remetente: variável de ambiente ARCELOR_SMTP_SENHA ou, se não existir,
        /// COMPRASGOV_SMTP_SENHA (mesmo remetente usado pelo ComprasGOV).
        /// </summary>
        public static string SmtpSenha => Env("ARCELOR_SMTP_SENHA", Environment.GetEnvironmentVariable("COMPRASGOV_SMTP_SENHA"));

        public const int DelayPadrao = 3000;
        public const int DelayLongo = 5000;
        public const int DelayLogin = 8000;
        /// <summary>Tempo MÁXIMO esperando a tabela de eventos aparecer (verifica a cada poucos segundos).</summary>
        public const int DelayTabela = 120000;
        public const int DelayDownload = 30000;
        /// <summary>Tempo máximo esperando a janela do evento abrir após o clique.</summary>
        public const int TimeoutJanelaEvento = 30000;

        // Quantas tentativas consecutivas sem achar número novo antes de considerar o fim da tabela.
        public const int MaxTentativasSemNovos = 12;

        // Seletores
        public const string SeletorUsuario = "#sap-user";
        public const string SeletorSenha = "#sap-password";
        public const string SeletorLogin = "#LOGON_BUTTON";
        public const string SeletorAvancCaption = "#SESSION_QUERY_CONTINUE_BUTTON-caption";
        public const string SeletorAvanc = "#SESSION_QUERY_CONTINUE_BUTTON";
        public const string SeletorCotacao = "#a_40000000005";
        public const string SeletorEventoHeader = "#WDE9";
        public const string SeletorTabela = "table.urST3BdBrd";
        public const string SeletorVisualizacaoCaption = "#WD20-caption";
        public const string SeletorVisualizacao = "#WD20";
        public const string SeletorScroll = "#WDE3-scrollV-Nxt";

        // Coluna "Status do evento"
        public const string StatusEncerrado = "Encerrado";

        public const string TextoAvanc = "Avanç.";
        public const string TextoEvento = "Nº evento";
        public const string TextoVisualizacao = "Visualização";
    }

    /// <summary>Uma conta (login) do portal. A senha pode ser nula no modo --login-manual.</summary>
    public sealed record ContaPortal(string Usuario, string Senha, string VariavelSenha);
}
