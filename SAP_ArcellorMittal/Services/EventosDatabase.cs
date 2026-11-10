using System;
using SAP_ArcellorMittal.Helpers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using SAP_ArcellorMittal.Models;

namespace SAP_ArcellorMittal.Services
{
    /// <summary>
    /// Banco JSON dos eventos já processados pelo robô. É a fonte principal de controle de "já processado".
    ///
    /// • Cada gravação relê o arquivo e mescla (outra execução pode ter gravado no meio tempo) e grava
    ///   num arquivo temporário que depois substitui o original (o .json nunca fica pela metade);
    ///   a versão anterior fica em ".bak".
    /// • Registros sem conta (importados do antigo numeros_eventos.txt) valem para todas as contas.
    /// </summary>
    public class EventosDatabase
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // mantém acentos legíveis no arquivo
        };

        private readonly string _caminho;
        private List<EventoProcessado> _eventos = new List<EventoProcessado>();

        public EventosDatabase(string caminho)
        {
            _caminho = caminho;
        }

        public string Caminho => _caminho;
        public IReadOnlyList<EventoProcessado> Eventos => _eventos;

        /// <summary>Carrega o banco (ou cria o arquivo vazio, se ainda não existe).</summary>
        public void Carregar()
        {
            if (File.Exists(_caminho))
            {
                _eventos = LerArquivo();
                Log.Detalhe($"🗄️ Banco JSON carregado: {_eventos.Count} evento(s) em '{_caminho}'.");
                return;
            }

            _eventos = new List<EventoProcessado>();
            Log.Detalhe($"🗄️ Banco JSON ainda não existe; criando em '{_caminho}'.");
            Gravar();
        }

        /// <summary>true se o evento já foi processado nesta conta (ou é um registro antigo, sem conta).</summary>
        public bool Contem(string conta, string numero) =>
            _eventos.Any(e => e.NumeroEvento == numero && (string.IsNullOrEmpty(e.Conta) || string.Equals(e.Conta, conta, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Números já processados para a conta (inclui os registros antigos, sem conta).</summary>
        public HashSet<string> NumerosDaConta(string conta) =>
            new HashSet<string>(_eventos
                .Where(e => string.IsNullOrEmpty(e.Conta) || string.Equals(e.Conta, conta, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.NumeroEvento));

        public List<EventoProcessado> EmailsPendentes() => _eventos.Where(e => e.EmailPendente).ToList();

        /// <summary>Insere ou atualiza (mesma conta + número) e grava o arquivo.</summary>
        public void Salvar(EventoProcessado evento)
        {
            Upsert(_eventos, evento);
            Gravar(evento);
        }

        private static void Upsert(List<EventoProcessado> lista, EventoProcessado evento)
        {
            int i = lista.FindIndex(e => e.NumeroEvento == evento.NumeroEvento &&
                                         string.Equals(e.Conta, evento.Conta, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) lista[i] = evento; else lista.Add(evento);
        }

        private List<EventoProcessado> LerArquivo()
        {
            // O arquivo fica num compartilhamento de rede: tenta algumas vezes se estiver momentaneamente bloqueado
            for (int tentativa = 1; ; tentativa++)
            {
                try
                {
                    var texto = File.ReadAllText(_caminho, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(texto)) return new List<EventoProcessado>();
                    return JsonSerializer.Deserialize<List<EventoProcessado>>(texto, Json) ?? new List<EventoProcessado>();
                }
                catch (IOException) when (tentativa < 5)
                {
                    Thread.Sleep(1000 * tentativa);
                }
            }
        }

        /// <summary>
        /// Relê o arquivo, aplica a alteração por cima (sem perder o que outra execução gravou) e substitui
        /// o arquivo de forma atômica.
        /// </summary>
        private void Gravar(EventoProcessado alterado = null)
        {
            var pasta = Path.GetDirectoryName(_caminho);
            if (!string.IsNullOrEmpty(pasta)) Directory.CreateDirectory(pasta);

            if (alterado != null && File.Exists(_caminho))
            {
                var doDisco = LerArquivo();
                // tudo o que só existe no disco é mantido; o que temos em memória prevalece
                foreach (var e in _eventos) Upsert(doDisco, e);
                _eventos = doDisco;
            }

            var temp = _caminho + ".tmp";
            for (int tentativa = 1; ; tentativa++)
            {
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(_eventos, Json), new UTF8Encoding(false));
                    if (File.Exists(_caminho))
                        File.Replace(temp, _caminho, _caminho + ".bak", ignoreMetadataErrors: true);
                    else
                        File.Move(temp, _caminho);
                    return;
                }
                catch (IOException) when (tentativa < 5)
                {
                    Thread.Sleep(1000 * tentativa);
                }
            }
        }
    }
}
