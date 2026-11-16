using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SAP_ArcellorMittal.Helpers
{
    /// <summary>
    /// Log do robô. Na tela aparecem só as etapas do fluxo (Info/Aviso/Erro), com horário.
    /// O arquivo diário robo_AAAAMMDD.log recebe TUDO, inclusive os detalhes (para diagnóstico).
    /// Com --detalhado, os detalhes também aparecem na tela.
    /// Rotação: um arquivo por dia; ao passar do tamanho máximo vira robo_AAAAMMDD.N.log; arquivos antigos
    /// são apagados na partida e a cada virada de dia. Senhas registradas com <see cref="RegistrarSegredo"/>
    /// nunca são gravadas (viram ***).
    /// </summary>
    public static class Log
    {
        public static bool Detalhado { get; set; }

        private static readonly object Trava = new object();
        private static string _pasta = Path.Combine(AppContext.BaseDirectory, "logs");
        private static int _diasGuardados = 30;
        private static long _tamanhoMaxBytes = 10L * 1024 * 1024;
        private static DateTime _ultimaLimpeza = DateTime.MinValue;
        private static readonly List<string> Segredos = new List<string>();

        public static string Pasta => _pasta;

        public static void Configurar(string pasta, int diasGuardados, int tamanhoMaxMb)
        {
            lock (Trava)
            {
                if (!string.IsNullOrWhiteSpace(pasta)) _pasta = pasta;
                if (diasGuardados > 0) _diasGuardados = diasGuardados;
                if (tamanhoMaxMb > 0) _tamanhoMaxBytes = tamanhoMaxMb * 1024L * 1024L;
            }
        }

        /// <summary>Para testes: tamanho máximo em bytes.</summary>
        public static void DefinirTamanhoMaxBytes(long bytes) { lock (Trava) _tamanhoMaxBytes = bytes; }

        /// <summary>Valor que nunca pode aparecer no log (senha do portal, senha SMTP, token).</summary>
        public static void RegistrarSegredo(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor) || valor.Length < 4) return;
            lock (Trava) if (!Segredos.Contains(valor)) Segredos.Add(valor);
        }

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
            lock (Trava)
            {
                foreach (var s in Segredos) texto = texto.Replace(s, "***");
                var agora = DateTime.Now;
                var linha = string.IsNullOrWhiteSpace(texto) ? "" : $"{agora:HH:mm:ss} {texto}";

                if (naTela)
                {
                    try { Console.WriteLine(linha); } catch { /* sem console (tarefa agendada) */ }
                }
                try
                {
                    Directory.CreateDirectory(_pasta);
                    if (_ultimaLimpeza.Date != agora.Date) { _ultimaLimpeza = agora; LimparAntigosSemTrava(); }

                    var arquivo = Path.Combine(_pasta, $"robo_{agora:yyyyMMdd}.log");
                    RotacionarSeGrande(arquivo, agora);
                    File.AppendAllText(arquivo, linha + Environment.NewLine);
                }
                catch { /* log em arquivo nunca derruba o robô */ }
            }
        }

        private static void RotacionarSeGrande(string arquivo, DateTime agora)
        {
            var info = new FileInfo(arquivo);
            if (!info.Exists || info.Length < _tamanhoMaxBytes) return;
            for (int n = 1; n < 1000; n++)
            {
                var destino = Path.Combine(_pasta, $"robo_{agora:yyyyMMdd}.{n}.log");
                if (File.Exists(destino)) continue;
                File.Move(arquivo, destino);
                return;
            }
        }

        /// <summary>Apaga arquivos de log mais antigos que o limite de dias.</summary>
        public static void LimparAntigos()
        {
            lock (Trava) LimparAntigosSemTrava();
        }

        private static void LimparAntigosSemTrava()
        {
            try
            {
                if (!Directory.Exists(_pasta)) return;
                foreach (var arquivo in Directory.GetFiles(_pasta, "robo_*.log")
                             .Where(a => File.GetLastWriteTime(a) < DateTime.Now.AddDays(-_diasGuardados)))
                    File.Delete(arquivo);
            }
            catch { }
        }
    }
}
