using System;
using System.IO;
using System.Linq;

namespace SAP_ArcellorMittal.Helpers
{
    /// <summary>
    /// Log do robô. Na tela aparecem só as etapas do fluxo (Info/Aviso/Erro), com horário.
    /// O arquivo diário logs\robo_AAAAMMDD.log recebe TUDO, inclusive os detalhes (para diagnóstico).
    /// Com --detalhado, os detalhes também aparecem na tela.
    /// </summary>
    public static class Log
    {
        public static bool Detalhado { get; set; }

        private static readonly object Trava = new object();
        private static readonly string PastaLogs = Path.Combine(AppContext.BaseDirectory, "logs");
        private const int DiasGuardados = 30;

        /// <summary>Etapa importante do fluxo (sempre na tela).</summary>
        public static void Info(string mensagem) => Escrever(mensagem, true);
        public static void Aviso(string mensagem) => Escrever(mensagem, true);
        public static void Erro(string mensagem) => Escrever(mensagem, true);

        /// <summary>Detalhe técnico: só no arquivo (e na tela com --detalhado).</summary>
        public static void Detalhe(string mensagem) => Escrever(mensagem, Detalhado);

        /// <summary>Título de uma etapa grande (linha em branco antes).</summary>
        public static void Secao(string titulo)
        {
            Escrever("", true);
            Escrever($"===== {titulo} =====", true);
        }

        private static void Escrever(string mensagem, bool naTela)
        {
            var texto = (mensagem ?? "").Trim('\r', '\n');
            var linha = string.IsNullOrWhiteSpace(texto) ? "" : $"{DateTime.Now:HH:mm:ss} {texto}";
            lock (Trava)
            {
                if (naTela) Console.WriteLine(linha);
                try
                {
                    Directory.CreateDirectory(PastaLogs);
                    File.AppendAllText(Path.Combine(PastaLogs, $"robo_{DateTime.Now:yyyyMMdd}.log"), linha + Environment.NewLine);
                }
                catch { /* log em arquivo nunca derruba o robô */ }
            }
        }

        /// <summary>Apaga arquivos de log com mais de 30 dias.</summary>
        public static void LimparAntigos()
        {
            try
            {
                if (!Directory.Exists(PastaLogs)) return;
                foreach (var arquivo in Directory.GetFiles(PastaLogs, "robo_*.log")
                             .Where(a => File.GetLastWriteTime(a) < DateTime.Now.AddDays(-DiasGuardados)))
                    File.Delete(arquivo);
            }
            catch { }
        }
    }
}
