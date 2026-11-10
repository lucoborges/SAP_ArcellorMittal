using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Services;
using SAP_ArcellorMittal.Helpers;

namespace SAP_ArcellorMittal
{
    /// <summary>Opções de linha de comando.</summary>
    public sealed class Opcoes
    {
        public bool SemPausa { get; init; }
        public bool LoginManual { get; init; }
        public int MaxEventos { get; init; } = int.MaxValue;
        public string SomenteConta { get; init; }
        public bool SemEmail { get; init; }
        public bool Continuo { get; init; }
        public int IntervaloMinutos { get; init; } = 60;
        public bool SegundoPlano { get; init; }
    }

    /// <summary>Sessão de navegador de uma conta, mantida aberta entre os ciclos do modo contínuo.</summary>
    internal sealed class SessaoConta
    {
        public IBrowserContext Contexto { get; init; }
        public IPage Pagina { get; set; }
    }

    class Program
    {
        /// <summary>
        /// Uso:
        ///   SAP_ArcellorMittal                       → PADRÃO: 24/7, verifica todas as contas de hora em hora, com as
        ///                                              janelas do navegador fora da tela (segundo plano). Ctrl+C para parar.
        ///   SAP_ArcellorMittal --intervalo-min 30    → intervalo entre as verificações (padrão 60)
        ///   SAP_ArcellorMittal --uma-vez             → verifica uma única vez e encerra (sem o ciclo 24/7)
        ///   SAP_ArcellorMittal --janela-visivel      → mantém as janelas do navegador na tela
        ///   SAP_ArcellorMittal --conta S0000000002   → processa só esta conta
        ///   SAP_ArcellorMittal --sem-email           → não envia e-mails (ficam pendentes no banco JSON)
        ///   SAP_ArcellorMittal --login-manual        → abre o portal e espera VOCÊ digitar usuário/senha na janela
        ///   SAP_ArcellorMittal --max-eventos 2       → processa no máximo 2 cotações por conta (para testes)
        ///   SAP_ArcellorMittal --sem-pausa           → com --uma-vez: sem "pressione qualquer tecla" no fim
        ///   SAP_ArcellorMittal --detalhado           → mostra também os detalhes técnicos na tela (sempre vão para logs\)
        /// </summary>
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            bool Tem(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
            string Valor(string flag)
            {
                int i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            Log.Detalhado = Tem("--detalhado");
            Log.LimparAntigos();

            var opcoes = new Opcoes
            {
                SemPausa = Tem("--sem-pausa"),
                LoginManual = Tem("--login-manual"),
                MaxEventos = int.TryParse(Valor("--max-eventos"), out var n) && n > 0 ? n : int.MaxValue,
                SomenteConta = Valor("--conta"),
                SemEmail = Tem("--sem-email"),
                // Padrão do sistema: 24/7 em segundo plano. (--continuo e --segundo-plano ainda são aceitos, sem efeito.)
                Continuo = !Tem("--uma-vez"),
                IntervaloMinutos = int.TryParse(Valor("--intervalo-min"), out var m) && m > 0 ? m : 60,
                SegundoPlano = !Tem("--janela-visivel"),
            };

            return await Automatizar(opcoes);
        }

        /// <summary>
        /// Abre o portal (e recarrega, como na versão original). Falhas de rede passageiras
        /// (ex.: net::ERR_NAME_NOT_RESOLVED) são repetidas algumas vezes com espera crescente.
        /// </summary>
        private static async Task<bool> AbrirPortalComRetentativas(IPage page, int tentativas = 5)
        {
            for (int t = 1; t <= tentativas; t++)
            {
                try
                {
                    await page.GotoAsync(Settings.UrlPortal);
                    await Task.Delay(6000);
                    await page.ReloadAsync();
                    return true;
                }
                catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_"))
                {
                    var espera = 5 * t;
                    Log.Aviso($"⚠️ Falha de rede ao abrir o portal (tentativa {t}/{tentativas}): {ex.Message.Split('\n')[0]}");
                    if (t < tentativas) await Task.Delay(espera * 1000);
                }
            }
            return false;
        }

        public static async Task<int> Automatizar(Opcoes opcoes)
        {
            int codigoSaida = 0;
            IPlaywright playwright = null;
            IBrowser browser = null;
            var sessoes = new Dictionary<string, SessaoConta>(StringComparer.OrdinalIgnoreCase);

            // Ctrl+C no modo contínuo: interrompe a espera atual e fecha o navegador com calma
            using var cancelar = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelar.Cancel(); Log.Aviso("⏹️ Parada solicitada (Ctrl+C)..."); };

            try
            {
                var contas = Settings.Contas;
                if (!string.IsNullOrWhiteSpace(opcoes.SomenteConta))
                    contas = contas.Where(c => c.Usuario.Equals(opcoes.SomenteConta, StringComparison.OrdinalIgnoreCase)).ToList();
                if (contas.Count == 0)
                {
                    Log.Erro($"❌ Nenhuma conta para processar{(string.IsNullOrWhiteSpace(opcoes.SomenteConta) ? "" : $" (--conta {opcoes.SomenteConta} não está configurada)")}.");
                    return codigoSaida = 1;
                }

                var email = new EmailService(opcoes.SemEmail);
                if (!opcoes.SemEmail && !email.Configurado)
                    Log.Aviso("⚠️ Senha SMTP não configurada (ARCELOR_SMTP_SENHA ou COMPRASGOV_SMTP_SENHA): os e-mails ficarão pendentes no banco.");

                Log.Info($"🤖 Robô ArcelorMittal — contas: {string.Join(", ", contas.Select(c => c.Usuario))}" +
                         (opcoes.Continuo ? $" — modo contínuo, a cada {opcoes.IntervaloMinutos} min" : ""));

                playwright = await Playwright.CreateAsync();

                for (int ciclo = 1; !cancelar.IsCancellationRequested; ciclo++)
                {
                    var inicioCiclo = DateTime.Now;
                    if (opcoes.Continuo) Log.Secao($"CICLO {ciclo} — {inicioCiclo:dd/MM/yyyy HH:mm}");

                    // O navegador pode ter sido fechado/derrubado entre os ciclos: abre de novo (as sessões se perdem)
                    if (browser == null || !browser.IsConnected)
                    {
                        if (browser != null) Log.Aviso("⚠️ O navegador foi fechado; abrindo de novo.");
                        sessoes.Clear();
                        browser = await AbrirNavegador(playwright);
                    }

                    int codigoCiclo;
                    try
                    {
                        codigoCiclo = await ExecutarCiclo(browser, contas, sessoes, email, opcoes);
                    }
                    catch (Exception ex)
                    {
                        // No modo contínuo um ciclo com erro nunca derruba o robô: tenta de novo no próximo
                        Log.Erro($"❌ Erro no ciclo: {ex.Message.Split('\n')[0]}");
                        codigoCiclo = 1;
                    }
                    codigoSaida = opcoes.Continuo ? 0 : codigoCiclo;

                    if (!opcoes.Continuo) break;

                    // Espera nunca negativa: Task.Delay(-1 ms) significa "esperar para sempre"
                    var espera = inicioCiclo.AddMinutes(opcoes.IntervaloMinutos) - DateTime.Now;
                    if (espera < TimeSpan.Zero) espera = TimeSpan.Zero;
                    Log.Info(espera == TimeSpan.Zero
                        ? "💤 A verificação passou do intervalo; a próxima começa agora."
                        : $"💤 Próxima verificação às {DateTime.Now + espera:HH:mm}.");
                    try { await Task.Delay(espera, cancelar.Token); }
                    catch (TaskCanceledException) { }
                }
            }
            catch (Exception ex)
            {
                Log.Erro($"❌ Erro geral: {ex.Message}");
                codigoSaida = 1;
            }
            finally
            {
                if (!opcoes.SemPausa && !opcoes.Continuo && !Console.IsInputRedirected)
                {
                    Console.WriteLine("\nPressione qualquer tecla para fechar...");
                    Console.ReadKey();
                }
                await SairDoPortal(sessoes);
                try { if (browser != null) await browser.CloseAsync(); } catch { }
                playwright?.Dispose();
            }

            return codigoSaida;
        }

        /// <summary>
        /// Logoff no SAP de cada conta ao encerrar. Sem isso a sessão fica "presa" no servidor e, na próxima
        /// execução, o portal responde "Query 'Publicado' já aberta em outro modo" e trava a lista.
        /// (Se o processo for morto à força, não dá tempo: a recuperação em SelecionarConsultaPublicado cuida disso.)
        /// </summary>
        private static async Task SairDoPortal(Dictionary<string, SessaoConta> sessoes)
        {
            foreach (var (usuario, sessao) in sessoes)
            {
                try
                {
                    var pagina = sessao.Pagina.IsClosed ? await sessao.Contexto.NewPageAsync() : sessao.Pagina;
                    await pagina.GotoAsync(Settings.UrlLogoff, new PageGotoOptions { Timeout = 15000 });
                    Log.Detalhe($"🚪 Logoff da conta {usuario} feito.");
                }
                catch (Exception ex)
                {
                    Log.Detalhe($"Logoff da conta {usuario} não concluído: {ex.Message.Split('\n')[0]}");
                }
            }
        }

        private static Task<IBrowser> AbrirNavegador(IPlaywright playwright) =>
            playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                SlowMo = 100, // pequena pausa entre ações: as telas Web Dynpro do SAP redesenham após cada evento
                // O Chromium desacelera páginas minimizadas/atrás de outras janelas; isto mantém o ritmo normal em segundo plano
                Args = new[]
                {
                    "--disable-background-timer-throttling",
                    "--disable-backgrounding-occluded-windows",
                    "--disable-renderer-backgrounding",
                }
            });

        /// <summary>Um ciclo: reenvia e-mails pendentes e verifica todas as contas. Retorna 0, 1 (falha grave) ou 2 (falhas parciais).</summary>
        private static async Task<int> ExecutarCiclo(IBrowser browser, List<ContaPortal> contas, Dictionary<string, SessaoConta> sessoes,
                                                     EmailService email, Opcoes opcoes)
        {
            int codigoSaida = 0;
            int novasNoCiclo = 0;

            // Relê o banco a cada ciclo (outra execução pode ter gravado no meio tempo)
            var banco = new EventosDatabase(Settings.ArquivoBancoEventos);
            banco.Carregar();
            await ReenviarEmailsPendentes(banco, email, opcoes.SemEmail);

            foreach (var conta in contas)
            {
                int codigoConta;
                try
                {
                    if (!sessoes.TryGetValue(conta.Usuario, out var sessao))
                    {
                        var contexto = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true });
                        // O SAP sempre enxerga a página como visível (mesmo atrás de outras janelas)
                        await contexto.AddInitScriptAsync(JanelaHelper.ScriptPaginaSempreVisivel);
                        if (opcoes.SegundoPlano)
                            contexto.Page += async (_, p) => await JanelaHelper.EnviarParaSegundoPlanoAsync(p); // janelas dos eventos
                        sessao = new SessaoConta { Contexto = contexto, Pagina = await contexto.NewPageAsync() };
                        sessoes[conta.Usuario] = sessao;
                    }
                    else if (sessao.Pagina.IsClosed)
                    {
                        sessao.Pagina = await sessao.Contexto.NewPageAsync();
                    }

                    var (codigo, novas) = await ProcessarConta(sessao, conta, banco, email, opcoes);
                    codigoConta = codigo;
                    novasNoCiclo += novas;
                }
                catch (Exception ex)
                {
                    Log.Erro($"❌ Erro geral na conta {conta.Usuario}: {ex.Message.Split('\n')[0]}");
                    codigoConta = 1;
                    // sessão em estado desconhecido: descarta, a próxima verificação abre uma nova
                    if (sessoes.Remove(conta.Usuario, out var quebrada))
                        try { await quebrada.Contexto.CloseAsync(); } catch { }
                }
                // 1 (falha grave) prevalece sobre 2 (alguns eventos com falha)
                if (codigoConta == 1 || (codigoConta == 2 && codigoSaida == 0)) codigoSaida = codigoConta;
            }

            var pendentes = banco.EmailsPendentes();
            Log.Info("");
            Log.Info($"🏁 Verificação concluída: {novasNoCiclo} cotação(ões) nova(s)" +
                     (pendentes.Count > 0 ? $"; ⚠️ {pendentes.Count} e-mail(s) pendente(s): {string.Join(", ", pendentes.Select(e => e.NumeroEvento))}" : "."));
            return codigoSaida;
        }

        private static async Task ReenviarEmailsPendentes(EventosDatabase banco, EmailService email, bool semEmail)
        {
            var pendentes = banco.EmailsPendentes();
            if (pendentes.Count == 0 || semEmail || !email.Configurado) return;

            Log.Info($"📧 Reenviando {pendentes.Count} e-mail(s) pendente(s)...");
            foreach (var evento in pendentes)
            {
                await email.TentarEnviarAsync(evento);
                banco.Salvar(evento);
            }
        }

        /// <summary>
        /// Processo completo de UMA conta, na sessão de navegador dela (cookies isolados e mantidos entre ciclos):
        /// login (só se a sessão expirou) → cotações → para cada cotação nova: PDF, banco JSON e e-mail.
        /// Retorna (0 = ok / 1 = falha grave / 2 = alguns eventos com falha, quantidade processada).
        /// </summary>
        private static async Task<(int Codigo, int Novas)> ProcessarConta(SessaoConta sessao, ContaPortal conta, EventosDatabase banco,
                                                                          EmailService email, Opcoes opcoes)
        {
            Log.Secao($"CONTA {conta.Usuario}");

            var context = sessao.Contexto;
            var page = sessao.Pagina;

            var processamentoService = new ProcessamentoService(context, page, conta, banco, email);
            var loginService = new LoginService(page);
            var navegacaoService = new NavegacaoService(page);
            var extracaoService = new ExtracaoService(page);

            // Já processados pelo robô (banco JSON) + finalizados pelo vendedor (planilha, só leitura)
            var baseNumeros = processamentoService.CarregarBaseNumeros();

            // Configura verificação de status (Encerrado / não identificado = ignorar)
            extracaoService.ConfigurarVerificadorIgnorar(processamentoService.EventoDeveSerIgnorado);

            if (!await AbrirPortalComRetentativas(page))
            {
                Log.Erro("❌ Não foi possível acessar o portal (rede/DNS).");
                return (1, 0);
            }
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle); } catch { }
            await Task.Delay(Settings.DelayPadrao);

            // Login: só quando o portal pede (no modo contínuo a sessão costuma continuar válida)
            if (await loginService.PrecisaLogin())
            {
                bool manual = opcoes.LoginManual || string.IsNullOrWhiteSpace(conta.Senha);
                if (manual && opcoes.SegundoPlano) await JanelaHelper.TrazerParaFrenteAsync(page); // a pessoa precisa ver a janela

                bool loginSucesso = manual
                    ? await loginService.AguardarLoginManual(conta)
                    : await loginService.RealizarLogin(conta);
                if (!loginSucesso)
                {
                    Log.Erro($"❌ Falha no login da conta {conta.Usuario}.");
                    return (1, 0);
                }
                if (!manual) Log.Info("🔐 Login feito.");
            }
            else
            {
                Log.Info("🔐 Sessão ainda válida (sem login).");
            }
            if (opcoes.SegundoPlano) await JanelaHelper.EnviarParaSegundoPlanoAsync(page);

            await VerificadorHelper.VerificarBotaoAvanc(page);

            if (!await navegacaoService.NavegarParaCotacoes())
            {
                Log.Erro("❌ Falha na navegação até as cotações.");
                return (1, 0);
            }

            var frameEncontrado = await extracaoService.LocalizarFrameEventos();
            if (frameEncontrado == null)
            {
                Log.Erro("❌ Tabela de eventos não encontrada.");
                return (1, 0);
            }

            int processados = 0;
            int totalEncontrados = 0;
            var falhas = new List<string>();
            var numerosAdicionados = new HashSet<string>();

            while (true)
            {
                if (totalEncontrados >= opcoes.MaxEventos)
                {
                    Log.Info($"ℹ️ Limite de {opcoes.MaxEventos} evento(s) atingido (--max-eventos).");
                    break;
                }

                var numeroNovo = await extracaoService.ProximoNumeroNaoExtraido(frameEncontrado, baseNumeros);
                if (numeroNovo == null) break;

                totalEncontrados++;
                Log.Info("");
                Log.Info($"🔍 Cotação nova: {numeroNovo}");

                bool sucesso = await processamentoService.ProcessarEvento(
                    numeroNovo,
                    extracaoService.FrameAtual ?? frameEncontrado,
                    baseNumeros,
                    numerosAdicionados
                );

                if (sucesso)
                {
                    processados++;
                    Log.Info($"✅ Cotação {numeroNovo} concluída.");
                }
                else
                {
                    falhas.Add(numeroNovo);
                    Log.Aviso($"⚠️ Cotação {numeroNovo} NÃO foi processada (tenta de novo na próxima verificação).");
                }
            }

            int codigoSaida = 0;
            if (extracaoService.TabelaIndisponivel)
            {
                Log.Erro("❌ A tabela de eventos ficou indisponível (sessão expirada ou tela recarregada); pode haver cotações não verificadas.");
                codigoSaida = 1;
            }
            if (falhas.Count > 0 && codigoSaida == 0) codigoSaida = 2;

            Log.Info($"📊 Conta {conta.Usuario}: {processados} cotação(ões) nova(s) processada(s)" +
                     (falhas.Count > 0 ? $", {falhas.Count} com falha ({string.Join(", ", falhas)})" : "") + ".");
            return (codigoSaida, processados);
        }
    }
}
