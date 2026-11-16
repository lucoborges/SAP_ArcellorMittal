using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SAP_ArcellorMittal.Config
{
    /// <summary>
    /// Configurações específicas da instalação (usuários do portal, pastas, e-mails, endereços e portas), lidas de
    /// config.local.json. O arquivo real NÃO vai para o Git; o modelo é config.exemplo.json.
    /// Onde o arquivo é procurado, em ordem: variável ARCELOR_CONFIG → ao lado do executável → pasta acima do
    /// executável (assim a configuração de produção fica fora da pasta que o deploy substitui).
    /// </summary>
    public sealed class ConfigLocal
    {
        public const string NomeArquivo = "config.local.json";

        /// <summary>Usuários do portal, na ordem em que são processados.</summary>
        public List<string> Contas { get; set; } = new List<string>();
        public string PlanilhaVendedor { get; set; }
        public string PastaBanco { get; set; }
        public string PastaSaida { get; set; }
        public string EmailRemetente { get; set; }
        public string EmailDestino { get; set; }
        public string SmtpHost { get; set; }
        public int SmtpPorta { get; set; } = 587;

        // Endereços do portal
        public string UrlPortal { get; set; }
        public string UrlLogoff { get; set; }

        /// <summary>Caminho do chrome.exe a usar. Vazio = Chromium instalado pelo Playwright.</summary>
        public string NavegadorExecutavel { get; set; }
        /// <summary>Minutos entre as verificações (0 = padrão de 60).</summary>
        public int IntervaloMinutos { get; set; }

        // Logs
        public string PastaLogs { get; set; }
        public int LogDiasGuardados { get; set; }
        public int LogTamanhoMaxMb { get; set; }

        // Rota de saúde (HTTP). Porta -1 desliga; 0 = padrão.
        public string SaudeEndereco { get; set; }
        public int SaudePorta { get; set; }

        /// <summary>Caminho do arquivo de configuração em uso (null se nenhum foi encontrado).</summary>
        public static string CaminhoEmUso { get; private set; }

        public static string Localizar()
        {
            var candidatos = new[]
            {
                Environment.GetEnvironmentVariable("ARCELOR_CONFIG"),
                Path.Combine(AppContext.BaseDirectory, NomeArquivo),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", NomeArquivo)),
            };
            foreach (var c in candidatos)
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
            return null;
        }

        public static ConfigLocal Carregar() => Carregar(CaminhoEmUso = Localizar());

        public static ConfigLocal Carregar(string caminho)
        {
            if (caminho == null || !File.Exists(caminho)) return new ConfigLocal();
            try
            {
                var opcoes = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                var config = JsonSerializer.Deserialize<ConfigLocal>(File.ReadAllText(caminho), opcoes) ?? new ConfigLocal();
                config.Contas ??= new List<string>();
                return config;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Não foi possível ler '{caminho}': {ex.Message}", ex);
            }
        }
    }
}
