using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SAP_ArcellorMittal.Helpers;

namespace SAP_ArcellorMittal.Services
{
    /// <summary>Fotografia do andamento do robô, usada pela rota de saúde.</summary>
    public sealed class EstadoSnapshot
    {
        public DateTime Inicio { get; init; }
        public int IntervaloMinutos { get; init; }
        public int Contas { get; init; }
        public int Ciclo { get; init; }
        public bool CicloEmAndamento { get; init; }
        public DateTime? FimUltimoCiclo { get; init; }
        public int? CodigoUltimoCiclo { get; init; }
        public int NovasUltimoCiclo { get; init; }
        public int FalhasGravesSeguidas { get; init; }
        public DateTime? ProximaVerificacao { get; init; }
    }

    /// <summary>Estado do robô em memória (atualizado a cada ciclo). Não guarda usuários nem dados das cotações.</summary>
    public static class EstadoRobo
    {
        private static readonly object Trava = new object();
        private static DateTime _inicio = DateTime.Now;
        private static int _intervalo = 60, _contas, _ciclo, _novas, _falhasSeguidas;
        private static bool _emAndamento;
        private static DateTime? _fimUltimo, _proxima;
        private static int? _codigoUltimo;

        public const int FalhasGravesParaAlerta = 3;

        public static void Reiniciar(int intervaloMinutos, int contas, DateTime? inicio = null)
        {
            lock (Trava)
            {
                _inicio = inicio ?? DateTime.Now; _intervalo = intervaloMinutos; _contas = contas;
                _ciclo = 0; _novas = 0; _falhasSeguidas = 0; _emAndamento = false;
                _fimUltimo = null; _proxima = null; _codigoUltimo = null;
            }
        }

        public static void IniciarCiclo(int ciclo)
        {
            lock (Trava) { _ciclo = ciclo; _emAndamento = true; }
        }

        public static void ConcluirCiclo(int codigo, int novas, DateTime? proxima, DateTime? fim = null)
        {
            lock (Trava)
            {
                _emAndamento = false; _fimUltimo = fim ?? DateTime.Now; _codigoUltimo = codigo; _novas = novas; _proxima = proxima;
                _falhasSeguidas = codigo == 1 ? _falhasSeguidas + 1 : 0;
            }
        }

        public static EstadoSnapshot Snapshot()
        {
            lock (Trava)
                return new EstadoSnapshot
                {
                    Inicio = _inicio, IntervaloMinutos = _intervalo, Contas = _contas, Ciclo = _ciclo,
                    CicloEmAndamento = _emAndamento, FimUltimoCiclo = _fimUltimo, CodigoUltimoCiclo = _codigoUltimo,
                    NovasUltimoCiclo = _novas, FalhasGravesSeguidas = _falhasSeguidas, ProximaVerificacao = _proxima,
                };
        }

        /// <summary>
        /// Saudável = terminou um ciclo há no máximo 2 intervalos + 30 min (ou acabou de subir) e não
        /// acumulou 3 ciclos seguidos com falha grave (portal fora, login recusado, tabela indisponível).
        /// </summary>
        public static (bool Saudavel, string Motivo) Avaliar(EstadoSnapshot e, DateTime agora)
        {
            var limite = TimeSpan.FromMinutes(e.IntervaloMinutos * 2 + 30);
            var referencia = e.FimUltimoCiclo ?? e.Inicio;
            if (agora - referencia > limite)
                return (false, e.FimUltimoCiclo.HasValue
                    ? $"nenhum ciclo concluído há {(int)(agora - referencia).TotalMinutes} min"
                    : $"nenhum ciclo concluído desde a partida, há {(int)(agora - referencia).TotalMinutes} min");
            if (e.FalhasGravesSeguidas >= FalhasGravesParaAlerta)
                return (false, $"{e.FalhasGravesSeguidas} ciclos seguidos com falha grave");
            return (true, e.FimUltimoCiclo.HasValue ? "ok" : "iniciando");
        }

        /// <summary>Versão do executável; o commit vem do sufixo "+hash" que o SDK grava na compilação.</summary>
        public static (string Versao, string Commit) VersaoECommit()
        {
            var info = typeof(EstadoRobo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var partes = info.Split('+', 2);
            return (partes[0], partes.Length > 1 ? partes[1] : "");
        }

        public static string MontarJson(DateTime agora)
        {
            var e = Snapshot();
            var (saudavel, motivo) = Avaliar(e, agora);
            var (versao, commit) = VersaoECommit();
#if DEBUG
            const string configuracao = "Debug";
#else
            const string configuracao = "Release";
#endif
            return JsonSerializer.Serialize(new
            {
                status = saudavel ? "ok" : "falha",
                motivo,
                versao,
                commit,
                configuracao,
                inicio = e.Inicio,
                contas = e.Contas,
                intervaloMinutos = e.IntervaloMinutos,
                ciclo = e.Ciclo,
                cicloEmAndamento = e.CicloEmAndamento,
                fimUltimoCiclo = e.FimUltimoCiclo,
                codigoUltimoCiclo = e.CodigoUltimoCiclo,
                novasUltimoCiclo = e.NovasUltimoCiclo,
                falhasGravesSeguidas = e.FalhasGravesSeguidas,
                proximaVerificacao = e.ProximaVerificacao,
            });
        }
    }

    /// <summary>
    /// Rota de saúde: GET /health (ou /saude) → 200 quando saudável, 503 quando não.
    /// Servidor HTTP mínimo sobre TcpListener (sem dependências e sem reserva de URL no Windows).
    /// Com token configurado, exige "X-Api-Key: token" ou "Authorization: Bearer token".
    /// Só aceita escutar fora de 127.0.0.1/localhost se houver token.
    /// </summary>
    public sealed class ServidorSaude : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly byte[] _token;

        public int Porta => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private ServidorSaude(IPAddress ip, int porta, string token)
        {
            _token = string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetBytes(token);
            _listener = new TcpListener(ip, porta);
        }

        public static ServidorSaude Iniciar(string endereco, int porta, string token)
        {
            var ip = string.Equals(endereco, "localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
                   : endereco == "*" ? IPAddress.Any
                   : IPAddress.Parse(endereco);
            if (!IPAddress.IsLoopback(ip) && string.IsNullOrEmpty(token))
                throw new InvalidOperationException(
                    $"a rota de saúde só escuta em {endereco} com ARCELOR_SAUDE_TOKEN definido (sem token, use 127.0.0.1)");

            var servidor = new ServidorSaude(ip, porta, token);
            servidor._listener.Start();
            _ = Task.Run(servidor.AceitarAsync);
            return servidor;
        }

        private async Task AceitarAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient cliente;
                try { cliente = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(() => AtenderAsync(cliente));
            }
        }

        private async Task AtenderAsync(TcpClient cliente)
        {
            using (cliente)
            {
                try
                {
                    cliente.ReceiveTimeout = cliente.SendTimeout = 5000;
                    using var stream = cliente.GetStream();
                    using var limite = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    limite.CancelAfter(5000);

                    // Lê só o cabeçalho (até a linha em branco), com teto de 8 KB
                    var buffer = new byte[8192];
                    int total = 0;
                    while (total < buffer.Length)
                    {
                        int lidos = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), limite.Token);
                        if (lidos == 0) break;
                        total += lidos;
                        if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n")) break;
                    }
                    var linhas = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n");
                    var (codigo, corpo) = Responder(linhas);

                    var bytesCorpo = Encoding.UTF8.GetBytes(corpo);
                    var texto = codigo switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Service Unavailable" };
                    var cabecalho = $"HTTP/1.1 {codigo} {texto}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                                    $"Content-Length: {bytesCorpo.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(cabecalho), limite.Token);
                    await stream.WriteAsync(bytesCorpo, limite.Token);
                }
                catch { /* cliente desistiu ou mandou lixo: ignora */ }
            }
        }

        private (int Codigo, string Corpo) Responder(string[] linhas)
        {
            var pedido = linhas.Length > 0 ? linhas[0].Split(' ') : Array.Empty<string>();
            if (pedido.Length < 2) return (404, "{\"erro\":\"pedido inválido\"}");
            if (pedido[0] != "GET") return (405, "{\"erro\":\"use GET\"}");

            var caminho = pedido[1].Split('?')[0].TrimEnd('/').ToLowerInvariant();
            if (caminho != "/health" && caminho != "/saude") return (404, "{\"erro\":\"rota não encontrada\"}");

            if (_token != null && !Autorizado(linhas)) return (401, "{\"erro\":\"não autorizado\"}");

            var agora = DateTime.Now;
            var (saudavel, _) = EstadoRobo.Avaliar(EstadoRobo.Snapshot(), agora);
            return (saudavel ? 200 : 503, EstadoRobo.MontarJson(agora));
        }

        private bool Autorizado(string[] linhas)
        {
            foreach (var linha in linhas.Skip(1))
            {
                int i = linha.IndexOf(':');
                if (i <= 0) continue;
                var nome = linha.Substring(0, i).Trim();
                var valor = linha.Substring(i + 1).Trim();
                if (nome.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    valor.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    valor = valor.Substring(7).Trim();
                else if (!nome.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(valor), _token)) return true;
            }
            return false;
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }
}
