using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;
using SAP_ArcellorMittal.Models;
using SAP_ArcellorMittal.Services.Pdf;

namespace SAP_ArcellorMittal.Services
{
    public class ProcessamentoService
    {
        private readonly IBrowserContext _context;
        private readonly IPage _page;
        private readonly ContaPortal _conta;
        private readonly EventosDatabase _banco;
        private readonly EmailService _email;

        public ProcessamentoService(IBrowserContext context, IPage page, ContaPortal conta, EventosDatabase banco, EmailService email)
        {
            _context = context;
            _page = page;
            _conta = conta;
            _banco = banco;
            _email = email;
        }

        /// <summary>
        /// Eventos que NÃO devem ser processados nesta conta: os do banco JSON (já processados pelo robô)
        /// + os da planilha do vendedor (cotações já finalizadas por ele; a planilha só é lida, nunca gravada).
        /// </summary>
        public HashSet<string> CarregarBaseNumeros()
        {
            var doBanco = _banco.NumerosDaConta(_conta.Usuario);
            var daPlanilha = CarregarNumerosDaPlanilha();

            var baseNumeros = new HashSet<string>(doBanco);
            foreach (var n in daPlanilha) baseNumeros.Add(n);

            Log.Detalhe($"📚 Base carregada: {doBanco.Count} do banco JSON + {daPlanilha.Count} finalizados na planilha do vendedor = {baseNumeros.Count} eventos únicos.");
            return baseNumeros;
        }

        private HashSet<string> CarregarNumerosDaPlanilha()
        {
            var numeros = new HashSet<string>();
            var caminho = Settings.CaminhoPlanilhaBase;

            try
            {
                if (!File.Exists(caminho))
                {
                    Log.Aviso($"⚠️ Planilha base não encontrada em '{caminho}'.");
                    return numeros;
                }

                // Só leitura, permitindo que a planilha esteja aberta no Excel pelo vendedor
                using var stream = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var workbook = new XLWorkbook(stream);
                var worksheet = ObterAba(workbook);
                if (worksheet == null)
                {
                    Log.Aviso("⚠️ Nenhuma aba válida encontrada na planilha base.");
                    return numeros;
                }

                int coluna = LocalizarColunaSolicitacao(worksheet);
                if (coluna <= 0)
                {
                    Log.Aviso($"⚠️ Coluna '{Settings.NomeColunaSolicitacao}' não encontrada na planilha base.");
                    return numeros;
                }

                var ultimaLinha = worksheet.LastRowUsed()?.RowNumber() ?? 0;
                for (int linha = 2; linha <= ultimaLinha; linha++)
                {
                    var valor = worksheet.Cell(linha, coluna).GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(valor))
                    {
                        numeros.Add(valor);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Erro($"❌ Erro ao ler a planilha base: {ex.Message}");
            }

            return numeros;
        }

        private static IXLWorksheet ObterAba(XLWorkbook workbook)
        {
            if (!string.IsNullOrWhiteSpace(Settings.NomeAbaBase))
            {
                return workbook.Worksheets.FirstOrDefault(w =>
                    string.Equals(w.Name, Settings.NomeAbaBase, StringComparison.OrdinalIgnoreCase));
            }

            return workbook.Worksheets.FirstOrDefault();
        }

        private static int LocalizarColunaSolicitacao(IXLWorksheet worksheet)
        {
            var primeiraLinha = worksheet.FirstRowUsed();
            if (primeiraLinha == null) return -1;

            foreach (var celula in primeiraLinha.CellsUsed())
            {
                var texto = celula.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(texto) &&
                    string.Equals(texto, Settings.NomeColunaSolicitacao, StringComparison.OrdinalIgnoreCase))
                {
                    return celula.Address.ColumnNumber;
                }
            }

            return -1;
        }

        public List<string> IdentificarNumerosNovos(HashSet<string> numerosEncontrados, HashSet<string> baseNumeros)
        {
            var numerosNovos = new List<string>();
            foreach (var num in numerosEncontrados)
            {
                if (!baseNumeros.Contains(num))
                {
                    numerosNovos.Add(num);
                }
            }
            return numerosNovos;
        }

        /// <summary>
        /// Verifica o status do evento na linha da tabela.
        /// Retorna true (deve ser ignorado) se o status for "Encerrado" OU se não for possível identificar o status.
        /// </summary>
        public async Task<bool> EventoDeveSerIgnorado(IFrame frame, string numero)
        {
            try
            {
                var status = await frame.EvaluateAsync<string>(@"
                    (numero) => {
                        const spans = Array.from(document.querySelectorAll('span.lsLink__text'));
                        const alvo = spans.find(s => s.textContent.trim() === numero);
                        if (!alvo) return null;

                        let tr = alvo.closest('tr');
                        if (!tr) return null;

                        const textos = Array.from(tr.querySelectorAll('span')).map(s => s.textContent.trim());
                        if (textos.includes('Encerrado')) return 'Encerrado';
                        if (textos.includes('Publicado')) return 'Publicado';

                        const cells = Array.from(tr.querySelectorAll('td'));
                        for (const c of cells) {
                            const t = c.textContent.trim();
                            if (t === 'Encerrado') return 'Encerrado';
                            if (t === 'Publicado') return 'Publicado';
                        }
                        return null;
                    }
                ", numero);

                if (string.IsNullOrWhiteSpace(status))
                {
                    Log.Detalhe($"   ⏭️ Status do evento {numero} não identificado. Ignorando por padrão.");
                    return true; // por padrão, pula
                }

                Log.Detalhe($"   📌 Status do evento {numero}: {status}");
                return string.Equals(status.Trim(), Settings.StatusEncerrado, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Aviso($"   ⚠️ Erro ao verificar status do evento {numero}: {ex.Message}. Ignorando por padrão.");
                return true; // em erro, pula por padrão
            }
        }

        /// <summary>
        /// Abre a cotação, baixa o PDF de "Visualização", lê o PDF, grava no banco JSON e envia o e-mail.
        /// Só marca o evento na base se o PDF foi baixado E lido com pelo menos um item.
        /// </summary>
        public async Task<bool> ProcessarEvento(string numero, IFrame frameEncontrado, HashSet<string> baseNumeros, HashSet<string> numerosAdicionados)
        {
            IPage janelaEvento = null;
            try
            {
                // Depois de fechar a janela de um evento o SAP costuma atualizar a lista: espera os links habilitarem
                if (!await ExtracaoService.AguardarTabelaPronta(frameEncontrado, Settings.DelayTabela))
                {
                    Log.Aviso($"   ⚠️ A lista de eventos não ficou disponível (links desabilitados). Evento NÃO processado.");
                    return false;
                }

                var candidatos = frameEncontrado.Locator("span.lsLink__text")
                    .Filter(new LocatorFilterOptions { HasTextRegex = new Regex($"^\\s*{Regex.Escape(numero)}\\s*$") });

                if (await candidatos.CountAsync() == 0)
                {
                    Log.Aviso($"   ⚠️ Link do evento {numero} não está mais na tabela.");
                    return false;
                }

                // Prefere o link VISÍVEL (o SAP pode ter cópias ocultas do texto)
                var link = candidatos.First;
                int total = await candidatos.CountAsync();
                for (int i = 0; i < total; i++)
                {
                    if (await candidatos.Nth(i).IsVisibleAsync()) { link = candidatos.Nth(i); break; }
                }

                // Clica no <a> do link (quem recebe o evento no SAP), não no texto dentro dele
                var ancora = link.Locator("xpath=ancestor::a[1]");
                if (await ancora.CountAsync() > 0) link = ancora;

                // Guarda as colunas da linha do evento (descrição, tipo, status, data final...) para o banco JSON
                var dadosPortal = await CapturarDadosPortal(frameEncontrado, numero);

                Log.Detalhe($"   🔗 Abrindo evento {numero}...");
                janelaEvento = await AbrirJanelaEvento(link, numero, frameEncontrado);
                if (janelaEvento == null)
                {
                    Log.Aviso($"   ⚠️ A janela do evento não abriu. Evento NÃO processado.");
                    return false;
                }

                await janelaEvento.WaitForLoadStateAsync(LoadState.NetworkIdle);
                Log.Detalhe($"   ✅ Janela do evento aberta.");

                var caminhoPdf = await BaixarPdfVisualizacao(janelaEvento, numero);
                if (caminhoPdf == null)
                {
                    Log.Erro($"   ❌ PDF do evento {numero} não foi baixado. NÃO será gravado na base.");
                    return false;
                }

                var cotacao = LerPdfEValidar(caminhoPdf, numero);
                if (cotacao == null) return false;

                // Registra no banco JSON (com o e-mail pendente) ANTES de enviar: se o e-mail falhar,
                // o evento não é reprocessado, mas o e-mail é tentado de novo na próxima execução.
                var registro = CriarRegistro(numero, dadosPortal, cotacao, caminhoPdf);
                _banco.Salvar(registro);
                numerosAdicionados.Add(numero);
                baseNumeros.Add(numero);
                Log.Detalhe($"   🗄️ Evento gravado no banco JSON ({_banco.Eventos.Count} eventos).");

                await _email.TentarEnviarAsync(registro);
                _banco.Salvar(registro); // grava o resultado do e-mail (enviado ou erro)
                return true;
            }
            catch (Exception ex)
            {
                Log.Erro($"   ❌ Erro ao processar evento {numero}: {ex.Message}. NÃO será gravado na base.");
                return false;
            }
            finally
            {
                if (janelaEvento != null)
                {
                    try { await janelaEvento.CloseAsync(); Log.Detalhe($"   ✅ Janela do evento fechada."); } catch { }
                }
                await VoltarParaPaginaPrincipal();
            }
        }

        private EventoProcessado CriarRegistro(string numero, Dictionary<string, string> dadosPortal, Cotacao cotacao, string caminhoPdf) =>
            new EventoProcessado
            {
                Conta = _conta.Usuario,
                NumeroEvento = numero,
                DadosPortal = dadosPortal ?? new Dictionary<string, string>(),
                Descricao = cotacao.Descricao,
                Versao = cotacao.Versao,
                Comprador = cotacao.Comprador,
                EmailComprador = cotacao.Email,
                TelefoneComprador = cotacao.Telefone,
                PrazoApresentacao = cotacao.PrazoApresentacao,
                DataAbertura = cotacao.DataAbertura,
                Itens = cotacao.Itens.Select(i => new ItemResumo
                {
                    NumeroItem = i.NumeroItem,
                    Quantidade = i.QuantidadeTexto,
                    Unidade = i.Unidade,
                    NumeroProduto = i.NumeroProduto,
                    Denominacao = i.Denominacao,
                    TextoLongo = i.TextoLongo,
                    DataRemessa = i.DataRemessa
                }).ToList(),
                ArquivoPdf = caminhoPdf,
                ProcessadoEm = DateTime.Now,
                Origem = "portal",
                EmailPendente = true
            };

        /// <summary>
        /// Lê as colunas da linha do evento na tabela do portal, usando os títulos das colunas como chave
        /// (mesmos nomes da exportação "todos_os_eventos": Descrição de eventos, Status do evento, Data final...).
        /// Em caso de falha devolve um dicionário vazio (não impede o processamento).
        /// </summary>
        public static async Task<Dictionary<string, string>> CapturarDadosPortal(IFrame frame, string numero)
        {
            try
            {
                // Na tabela do SAP (table.urST3BdBrd) o cabeçalho é uma linha de <th> no mesmo corpo da tabela,
                // a 1ª célula de cada linha (seleção) também é <th> e o link pode estar numa tabela interna.
                // Por isso: sobe do link até a <tr> que pertence à tabela da lista e casa as células pela posição.
                // O resultado volta como texto JSON: o Playwright não converte objeto JS em Dictionary (chegava vazio).
                var json = await frame.EvaluateAsync<string>(@"(numero) => JSON.stringify(((numero) => {
                    const limpar = t => (t || '').replace(/\s+/g, ' ').trim();
                    const spans = Array.from(document.querySelectorAll('span.lsLink__text'))
                        .filter(s => s.textContent.trim() === numero);
                    if (spans.length === 0) return { __erro: 'link do evento não encontrado' };
                    const span = spans.find(s => s.getBoundingClientRect().height > 0) || spans[0];

                    let linha = null, tabela = null;
                    for (let el = span.parentElement; el; el = el.parentElement) {
                        if (el.tagName !== 'TR') continue;
                        const t = el.closest('table');
                        if (t && t.classList.contains('urST3BdBrd')) { linha = el; tabela = t; break; }
                    }
                    if (!linha) return { __erro: 'linha da tabela da lista (table.urST3BdBrd) não encontrada' };

                    const celulasDe = tr => Array.from(tr.children).filter(c => c.tagName === 'TD' || c.tagName === 'TH');
                    const texto = c => limpar(c.innerText || c.textContent);
                    // Cabeçalho: a linha da MESMA tabela que só tem <th> e contém 'Nº evento'
                    const cabecalho = Array.from(tabela.querySelectorAll('tr'))
                        .filter(tr => tr.closest('table') === tabela)
                        .find(tr => celulasDe(tr).every(c => c.tagName === 'TH') && celulasDe(tr).some(c => texto(c) === 'Nº evento'));
                    if (!cabecalho) return { __erro: 'cabeçalho com Nº evento não encontrado' };

                    const titulos = celulasDe(cabecalho).map(texto);
                    const valores = celulasDe(linha).map(texto);
                    const r = {};
                    titulos.forEach((t, i) => { if (t && i < valores.length && !(t in r)) r[t] = valores[i]; });
                    if (r['Nº evento'] !== numero)
                        return { __erro: `colunas desalinhadas: Nº evento='${r['Nº evento']}' | títulos=${titulos.join('|')} | valores=${valores.join('|')}` };
                    return r;
                })(numero))", numero);

                var dados = string.IsNullOrWhiteSpace(json)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                if (dados.TryGetValue("__erro", out var erro))
                {
                    Log.Aviso($"   ⚠️ Colunas do evento {numero} na tabela não lidas: {erro}");
                    return new Dictionary<string, string>();
                }
                Log.Detalhe($"   📋 Colunas do portal lidas: {dados.Count} ({string.Join(", ", dados.Keys)})");
                return dados;
            }
            catch (Exception ex)
            {
                Log.Aviso($"   ⚠️ Não foi possível ler as colunas do evento {numero} na tabela: {ex.Message.Split('\n')[0]}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Clica no número do evento e devolve a janela nova. Tenta: clique normal (o Playwright espera o
        /// elemento ficar estável depois da rolagem), clique forçado (como a versão antiga) e evento de clique
        /// via JavaScript. Se nada abrir, salva um diagnóstico (print + elementos) para análise.
        /// </summary>
        private async Task<IPage> AbrirJanelaEvento(ILocator link, string numero, IFrame frame)
        {
            var tentativas = new (string Nome, Func<Task> Clique)[]
            {
                ("clique normal", async () => await link.ClickAsync(new LocatorClickOptions { Timeout = 10000 })),
                ("clique forçado", async () => await link.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 10000 })),
                ("clique via JavaScript", async () => await link.EvaluateAsync("e => (e.closest('a') || e).click()")),
            };

            var paginasAntes = new HashSet<IPage>(_context.Pages);
            var log = new List<string>();

            foreach (var (nome, clique) in tentativas)
            {
                try
                {
                    await link.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 });
                    await Task.Delay(700); // deixa a tabela do SAP terminar de redesenhar após a rolagem

                    var pagina = await _context.RunAndWaitForPageAsync(clique,
                        new BrowserContextRunAndWaitForPageOptions { Timeout = Settings.TimeoutJanelaEvento / 2 });
                    if (nome != "clique normal") Log.Detalhe($"   ℹ️ Janela aberta com {nome}.");
                    return pagina;
                }
                catch (Exception ex)
                {
                    log.Add($"{nome}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                    // A janela pode ter aberto sem o evento ter sido capturado
                    var nova = _context.Pages.FirstOrDefault(p => !paginasAntes.Contains(p) && !p.IsClosed);
                    if (nova != null)
                    {
                        Log.Detalhe($"   ℹ️ Janela nova detectada após {nome}.");
                        return nova;
                    }
                    Log.Detalhe($"   ↻ {nome} não abriu a janela; tentando outra forma...");
                }
            }

            await SalvarDiagnostico(numero, frame, log);
            return null;
        }

        /// <summary>Salva print da tela e informações dos elementos para entender por que a janela não abriu.</summary>
        private async Task SalvarDiagnostico(string numero, IFrame frame, List<string> log)
        {
            try
            {
                var pasta = Path.Combine(Settings.PastaSaida, "diagnostico", $"{DateTime.Now:yyyyMMdd_HHmmss}_{numero}");
                Directory.CreateDirectory(pasta);

                var info = new List<string> { $"Evento: {numero}", "", "Tentativas:" };
                info.AddRange(log.Select(l => "  " + l));

                info.Add(""); info.Add("Páginas abertas:");
                foreach (var p in _context.Pages) info.Add($"  {p.Url}");

                info.Add(""); info.Add("Frames da página principal:");
                foreach (var f in _page.Frames) info.Add($"  {(f == frame ? "[TABELA] " : "")}{f.Name} | {f.Url}");

                info.Add(""); info.Add("Elementos com o número do evento (no frame da tabela):");
                var detalhes = await frame.EvaluateAsync<string[]>(@"(numero) => {
                    return Array.from(document.querySelectorAll('span.lsLink__text'))
                      .filter(s => s.textContent.trim() === numero)
                      .map((s, i) => {
                        const r = s.getBoundingClientRect();
                        const a = s.closest('a');
                        const topo = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
                        return `#${i} visível=${r.width > 0 && r.height > 0} caixa=(${Math.round(r.x)},${Math.round(r.y)},${Math.round(r.width)}x${Math.round(r.height)})` +
                               ` | no ponto do clique está: ${topo ? topo.tagName + '.' + topo.className + ' ' + (topo.textContent || '').trim().slice(0, 30) : 'nada'}` +
                               `\n     link: ${a ? a.outerHTML.slice(0, 400) : '(sem <a>)'}`;
                      });
                }", numero);
                info.AddRange(detalhes.Select(d => "  " + d));

                await File.WriteAllLinesAsync(Path.Combine(pasta, "info.txt"), info);
                await _page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(pasta, "tela.png"), FullPage = true });
                foreach (var (p, i) in _context.Pages.Select((p, i) => (p, i)).Where(x => x.p != _page))
                    await p.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(pasta, $"janela_{i}.png") });

                Log.Detalhe($"   🩺 Diagnóstico salvo em: {pasta}");
            }
            catch (Exception ex)
            {
                Log.Aviso($"   ⚠️ Não foi possível salvar o diagnóstico: {ex.Message}");
            }
        }

        /// <summary>
        /// Lê o PDF e imprime o resumo. Retorna null (= falha, não gravar) se o PDF não pôde ser lido,
        /// não tem itens, não tem o número da solicitação ou é de outra solicitação que não a esperada.
        /// </summary>
        public static Cotacao LerPdfEValidar(string caminhoPdf, string numeroEsperado = null)
        {
            Cotacao cotacao;
            try
            {
                cotacao = PdfCotacaoParser.LerArquivo(caminhoPdf);
            }
            catch (Exception ex)
            {
                Log.Erro($"   ❌ Erro ao ler o PDF '{caminhoPdf}': {ex.Message}");
                return null;
            }

            ImprimirResumo(cotacao);

            if (string.IsNullOrWhiteSpace(cotacao.NumeroSolicitacao))
            {
                Log.Erro($"   ❌ O PDF '{caminhoPdf}' não tem o número da solicitação de cotação.");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(numeroEsperado) && cotacao.NumeroSolicitacao != numeroEsperado)
            {
                Log.Erro($"   ❌ O PDF é da solicitação {cotacao.NumeroSolicitacao}, mas o evento clicado foi {numeroEsperado}.");
                return null;
            }

            if (cotacao.Itens.Count == 0)
            {
                Log.Erro($"   ❌ Nenhum item encontrado no PDF '{caminhoPdf}'.");
                return null;
            }
            return cotacao;
        }

        private static void ImprimirResumo(Cotacao c)
        {
            Log.Info($"   📄 Solicitação {c.NumeroSolicitacao} v{c.Versao} — {c.Descricao}");
            Log.Info($"      Prazo: {c.PrazoApresentacao:dd/MM/yyyy HH:mm} | Comprador: {c.Comprador} | {c.Itens.Count} item(ns)");
            foreach (var i in c.Itens)
                Log.Detalhe($"      • Item {i.NumeroItem}: {i.QuantidadeTexto} {i.Unidade} — {i.Denominacao} ({i.NumeroProduto})");
            foreach (var a in c.Avisos)
                Log.Detalhe($"      ⚠️ {a}");
        }

        /// <summary>Clica em "Visualização" e salva o PDF como {numero}.pdf. Retorna o caminho ou null.</summary>
        private async Task<string> BaixarPdfVisualizacao(IPage pagina, string numero)
        {
            var botao = await LocalizarBotaoVisualizacao(pagina);
            if (botao == null)
            {
                Log.Aviso($"   ⚠️ Botão 'Visualização' não encontrado.");
                return null;
            }

            Directory.CreateDirectory(Settings.PastaPdfs);
            var destino = Path.Combine(Settings.PastaPdfs, $"{numero}.pdf");

            Log.Detalhe($"   🔗 Clicando em 'Visualização' para baixar o PDF...");

            // O SAP pode entregar o PDF como download ou abrindo uma nova aba: espera pelos dois
            var esperaDownload = pagina.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = Settings.DelayDownload });
            var esperaAba = _context.WaitForPageAsync(new BrowserContextWaitForPageOptions { Timeout = Settings.DelayDownload });
            // evita "UnobservedTaskException" da espera que não ganhar a corrida
            _ = esperaDownload.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = esperaAba.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            try
            {
                await botao.ClickAsync(new LocatorClickOptions { Timeout = 10000 });
            }
            catch (TimeoutException)
            {
                Log.Detalhe("   ↻ Clique normal em 'Visualização' bloqueado; usando clique forçado...");
                await botao.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 10000 });
            }

            // Task.WhenAny retorna assim que a primeira termina, inclusive por timeout:
            // se a primeira falhou, ainda damos chance à outra.
            var primeiro = await Task.WhenAny(esperaDownload, esperaAba);
            if (primeiro.Status != TaskStatus.RanToCompletion)
            {
                var outra = primeiro == esperaDownload ? (Task)esperaAba : esperaDownload;
                try { await outra; } catch { }
                primeiro = outra;
            }

            if (primeiro == esperaDownload && esperaDownload.Status == TaskStatus.RanToCompletion)
            {
                await esperaDownload.Result.SaveAsAsync(destino);
                return ValidarPdf(destino);
            }

            if (primeiro == esperaAba && esperaAba.Status == TaskStatus.RanToCompletion)
            {
                var aba = esperaAba.Result;
                try
                {
                    // A aba nova pode disparar um download logo em seguida
                    var tardio = await Task.WhenAny(esperaDownload, Task.Delay(Settings.DelayLongo));
                    if (tardio == esperaDownload && esperaDownload.Status == TaskStatus.RanToCompletion)
                    {
                        await esperaDownload.Result.SaveAsAsync(destino);
                        return ValidarPdf(destino);
                    }

                    await aba.WaitForLoadStateAsync(LoadState.Load);
                    Log.Detalhe($"   ℹ️ PDF aberto numa aba ({aba.Url}). Baixando pelo endereço...");
                    var resposta = await _context.APIRequest.GetAsync(aba.Url);
                    await File.WriteAllBytesAsync(destino, await resposta.BodyAsync());
                    return ValidarPdf(destino);
                }
                catch (Exception ex)
                {
                    Log.Aviso($"   ⚠️ Não foi possível obter o PDF da aba aberta: {ex.Message}");
                    return null;
                }
                finally
                {
                    try { await aba.CloseAsync(); } catch { }
                }
            }

            Log.Aviso($"   ⚠️ O PDF não chegou em {Settings.DelayDownload / 1000}s (nem download, nem nova aba).");
            return null;
        }

        private static string ValidarPdf(string caminho)
        {
            try
            {
                var cabecalho = new byte[4];
                int lidos;
                using (var fs = File.OpenRead(caminho))
                    lidos = fs.Read(cabecalho, 0, 4);

                if (lidos == 4 && cabecalho[0] == '%' && cabecalho[1] == 'P' && cabecalho[2] == 'D' && cabecalho[3] == 'F')
                {
                    Log.Info($"   📥 PDF salvo: {caminho}");
                    return caminho;
                }
                Log.Aviso($"   ⚠️ O arquivo baixado não é um PDF válido: {caminho}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Aviso($"   ⚠️ Erro ao validar PDF: {ex.Message}");
                return null;
            }
        }

        private static async Task<ILocator> LocalizarBotaoVisualizacao(IPage pagina)
        {
            var texto = new Regex($"^\\s*{Regex.Escape(Settings.TextoVisualizacao)}\\s*$");
            // A tela do evento pode demorar a montar: tenta por alguns segundos
            for (int tentativa = 0; tentativa < 15; tentativa++)
            {
                foreach (var frame in pagina.Frames)
                {
                    try
                    {
                        // O clique tem de ir na "caixa" do botão (div ct="B"), que é quem recebe o evento no SAP;
                        // o texto (span) fica por baixo dela.
                        var porTexto = frame.Locator("span.lsButton__text").Filter(new LocatorFilterOptions { HasTextRegex = texto }).First;
                        if (await porTexto.CountAsync() > 0)
                        {
                            var caixa = porTexto.Locator("xpath=ancestor::div[@ct='B'][1]");
                            return await caixa.CountAsync() > 0 ? caixa : porTexto;
                        }

                        var porId = frame.Locator(Settings.SeletorVisualizacaoCaption).Filter(new LocatorFilterOptions { HasTextRegex = texto }).First;
                        if (await porId.CountAsync() > 0)
                        {
                            var caixa = frame.Locator(Settings.SeletorVisualizacao);
                            return await caixa.CountAsync() > 0 ? caixa : porId;
                        }
                    }
                    catch { /* o frame pode ter sido descartado enquanto a tela carregava */ }
                }
                await Task.Delay(1000);
            }
            return null;
        }

        private async Task VoltarParaPaginaPrincipal()
        {
            try
            {
                var principal = _context.Pages.First();
                await principal.BringToFrontAsync();
                await Task.Delay(1000);
            }
            catch { }
        }
    }
}
