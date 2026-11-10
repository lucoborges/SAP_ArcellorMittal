using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SAP_ArcellorMittal.Config
{
    /// <summary>
    /// Configurações específicas da instalação (usuários do portal, pastas, e-mails), lidas de
    /// config.local.json ao lado do executável. O arquivo real NÃO vai para o Git; o modelo é config.exemplo.json.
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

        public static ConfigLocal Carregar()
        {
            var caminho = Path.Combine(AppContext.BaseDirectory, NomeArquivo);
            if (!File.Exists(caminho)) return new ConfigLocal();
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
