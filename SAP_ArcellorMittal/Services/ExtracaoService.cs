using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;

namespace SAP_ArcellorMittal.Services
{
    public class ExtracaoService
    {
        private readonly IPage _page;
        private IFrame _frameEncontrado;
        private bool _fimTabela = false;

        /// <summary>
        /// true quando a tabela de eventos deixou de responder (sessão expirada, frame recarregado...).
        /// Nesse caso o fim do processamento NÃO significa "não há cotações novas".
        /// </summary>
        public bool TabelaIndisponivel { get; private set; }

        /// <summary>Frame atual da tabela (pode mudar se a tabela precisar ser localizada de novo).</summary>
        public IFrame FrameAtual => _frameEncontrado;
        private HashSet<string> _numerosJaExtraidos = new HashSet<string>();

        // Contador de tentativas consecutivas sem encontrar número novo,
        // para insistir um pouco antes de declarar o fim da tabela.
        private int _tentativasSemNovos = 0;

        // Callback opcional para verificar se um evento deve ser ignorado (Encerrado ou status não identificado)
        private Func<IFrame, string, Task<bool>> _verificarIgnorar;

        public ExtracaoService(IPage page)
        {
            _page = page;
        }

        /// <summary>
        /// Registra o callback que verifica se o evento deve ser ignorado.
        /// </summary>
        public void ConfigurarVerificadorIgnorar(Func<IFrame, string, Task<bool>> verificador)
        {
            _verificarIgnorar = verificador;
        }

        /// <summary>
        /// A tabela (POWL do SAP SRM) só aceita cliques depois que a consulta termina de atualizar.
        /// Enquanto aparece "(Operação de atualização em execução)", os números ficam com a classe
        /// "lsLink__text--disabled" e o clique não faz nada.
        /// </summary>
        public static async Task<bool> TabelaPronta(IFrame frame)
        {
            try
            {
                return await frame.EvaluateAsync<bool>(@"() => {
                    if ((document.body.innerText || '').includes('Operação de atualização em execução')) return false;
                    const numeros = Array.from(document.querySelectorAll('span.lsLink__text'))
                        .filter(s => /^\d{10}$/.test(s.textContent.trim()));
                    return numeros.length > 0 && !numeros.some(s => s.classList.contains('lsLink__text--disabled'));
                }");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Espera a tabela ficar pronta (ver <see cref="TabelaPronta"/>). Retorna false se o tempo acabar.</summary>
        public static async Task<bool> AguardarTabelaPronta(IFrame frame, int timeoutMs)
        {
            var limite = DateTime.Now.AddMilliseconds(timeoutMs);
            bool avisou = false;
            while (DateTime.Now < limite)
            {
                if (frame.IsDetached) return false;
                if (await TabelaPronta(frame)) return true;
                if (!avisou) { Log.Detalhe("   ⏳ O SAP está atualizando a lista; aguardando terminar..."); avisou = true; }
                await Task.Delay(2000);
            }
            return false;
        }

        /// <summary>
        /// "Retrato" da tabela: null enquanto o SAP está atualizando (mensagem de atualização ou links desabilitados);
        /// senão, os números listados + os textos das consultas ("Publicado (19)"...). Se o retrato não muda entre
        /// leituras seguidas, a atualização terminou de verdade.
        /// </summary>
        private static async Task<string> RetratoTabela(IFrame frame, bool exigirEventos)
        {
            try
            {
                return await frame.EvaluateAsync<string>(@"(exigirEventos) => {
                    if ((document.body.innerText || '').includes('Operação de atualização em execução')) return null;
                    // Só os links dos NÚMEROS contam: o SAP tem outros links que ficam sempre desabilitados
                    const links = Array.from(document.querySelectorAll('span.lsLink__text'))
                        .filter(s => /^\d{10}$/.test(s.textContent.trim()));
                    if (links.some(s => s.classList.contains('lsLink__text--disabled'))) return null;
                    const numeros = links.map(s => s.textContent.trim());
                    if (exigirEventos && numeros.length === 0) return null;
                    const consultas = Array.from(document.querySelectorAll('a, span.lsLink__text'))
                        .map(e => e.textContent.trim()).filter(t => /^\S.*\(\d+\)$/.test(t));
                    return numeros.join(',') + '|' + Array.from(new Set(consultas)).join(',');
                }", exigirEventos);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Espera a tabela terminar de atualizar E ficar estável (mesmo retrato em 3 leituras seguidas, ~4s).
        /// Clicar em "Publicado" enquanto o SAP ainda atualiza a lista trava o portal sem nenhum evento.
        /// </summary>
        public static async Task<bool> AguardarTabelaEstavel(IFrame frame, int timeoutMs, bool exigirEventos = true)
        {
            var limite = DateTime.Now.AddMilliseconds(timeoutMs);
            string anterior = null;
            int iguais = 0;
            while (DateTime.Now < limite)
            {
                if (frame.IsDetached) return false;
                var retrato = await RetratoTabela(frame, exigirEventos);
                iguais = retrato != null && retrato == anterior ? iguais + 1 : 0;
                if (iguais >= 2) return true;
                anterior = retrato;
                await Task.Delay(2000);
            }
            return false;
        }

        /// <summary>
        /// Clica na consulta "Publicado (N)" de "Licitações na Web": a tabela passa a mostrar só as cotações
        /// publicadas (sem precisar rolar pelas centenas de encerradas). Só clica depois que a lista terminou de
        /// atualizar. Se o portal "travar" (lista não carrega ou o contador zera: "Publicado (19)" → "Publicado (0)"),
        /// volta para "Todos(as)", espera estabilizar e tenta de novo (até 3 vezes). Se não der, fica com
        /// "Todos(as)" — funciona igual, só é mais lento (o robô já pula os eventos encerrados).
        /// </summary>
        private async Task SelecionarConsultaPublicado(IFrame frame)
        {
            const int tentativas = 3;
            int esperado = -1; // maior contador de "Publicado (N)" já visto nesta tela
            for (int tentativa = 1; tentativa <= tentativas; tentativa++)
            {
                try
                {
                    if (!await AguardarTabelaEstavel(frame, Settings.DelayTabela))
                        Log.Aviso("⚠️ A lista de eventos não estabilizou; selecionando 'Publicado' mesmo assim.");

                    var (consulta, texto, quantidade) = await LocalizarConsulta(frame, @"^\s*Publicado\s*\(\d+\)\s*$");
                    if (consulta == null)
                    {
                        Log.Detalhe("ℹ️ Consulta 'Publicado' não encontrada; usando a lista completa.");
                        return;
                    }
                    esperado = Math.Max(esperado, quantidade);

                    if (!(quantidade == 0 && esperado > 0)) // "Publicado (0)" depois de já ter visto N > 0 = portal travado
                    {
                        Log.Detalhe($"🔎 Selecionando a consulta '{texto}' (tentativa {tentativa}/{tentativas})...");
                        await consulta.ClickAsync();
                        await Task.Delay(Settings.DelayPadrao);

                        // Com "Publicado (0)" de verdade a lista fica vazia de propósito: não exige eventos
                        bool estavel = await AguardarTabelaEstavel(frame, 60000, exigirEventos: quantidade != 0);
                        var (_, textoDepois, quantidadeDepois) = await LocalizarConsulta(frame, @"^\s*Publicado\s*\(\d+\)\s*$");
                        if (estavel && !(quantidadeDepois == 0 && esperado > 0))
                        {
                            Log.Info($"📋 Lista '{textoDepois ?? texto}' carregada.");
                            return;
                        }
                        Log.Aviso($"⚠️ A lista 'Publicado' travou (antes '{texto}', agora '{textoDepois}') — tentativa {tentativa}/{tentativas}.");
                    }
                    else
                    {
                        Log.Aviso($"⚠️ O portal mostra '{texto}', mas havia {esperado} publicadas — tentativa {tentativa}/{tentativas}.");
                    }
                    await SalvarPrint(frame, $"publicado_tentativa{tentativa}");

                    // Recupera: volta para "Todos(as)" e deixa o SAP terminar de atualizar antes de tentar de novo
                    var (todos, textoTodos, _) = await LocalizarConsulta(frame, @"^\s*Todos.*\(\d+\)\s*$");
                    if (todos != null)
                    {
                        Log.Detalhe($"↻ Voltando para '{textoTodos}'...");
                        await todos.ClickAsync();
                        await Task.Delay(Settings.DelayPadrao);
                    }
                }
                catch (Exception ex)
                {
                    Log.Aviso($"⚠️ Erro ao selecionar a consulta 'Publicado' (tentativa {tentativa}/{tentativas}): {ex.Message.Split('\n')[0]}");
                }
                await Task.Delay(Settings.DelayLongo);
            }

            await AguardarTabelaEstavel(frame, Settings.DelayTabela);
            Log.Aviso("⚠️ Seguindo com a lista 'Todos(as)' (mais lenta, mas o robô pula os encerrados).");
        }

        /// <summary>
        /// Consulta da linha "Licitações na Web" ("Publicado (19)", "Todos(as) (1300)"...).
        /// A linha de baixo, "Leilões na Web", tem consultas com os MESMOS nomes, e a consulta selecionada
        /// deixa de ser link (fica destacada). Por isso a busca é por qualquer elemento, pegando o primeiro
        /// que vem DEPOIS do rótulo "Licitações na Web" na página (a linha de Leilões vem depois).
        /// </summary>
        public static async Task<(ILocator Link, string Texto, int Quantidade)> LocalizarConsulta(IFrame frame, string padrao)
        {
            var texto = await frame.EvaluateAsync<string>(@"(padrao) => {
                document.querySelectorAll('[data-robo-consulta]').forEach(e => e.removeAttribute('data-robo-consulta'));
                const re = new RegExp(padrao);
                const maisInterno = (lista, teste) => lista.filter(e => teste(e) && !Array.from(e.children).some(teste));
                const todos = Array.from(document.body.querySelectorAll('*'));
                const rotulo = maisInterno(todos, e => e.textContent.trim() === 'Licitações na Web')[0];
                const candidatos = maisInterno(todos, e => re.test(e.textContent.trim()))
                    .filter(e => !rotulo || (rotulo.compareDocumentPosition(e) & Node.DOCUMENT_POSITION_FOLLOWING));
                const alvo = candidatos[0];
                if (!alvo) return null;
                (alvo.closest('a') || alvo).setAttribute('data-robo-consulta', '1');
                return alvo.textContent.trim();
            }", padrao);
            if (texto == null) return (null, null, -1);
            var quantidade = int.TryParse(Regex.Match(texto, @"\((\d+)\)\s*$").Groups[1].Value, out var q) ? q : -1;
            return (frame.Locator("[data-robo-consulta='1']").First, texto, quantidade);
        }

        /// <summary>Imagem da tela para entender travamentos do portal (pasta diagnostico, ao lado dos PDFs).</summary>
        private static async Task SalvarPrint(IFrame frame, string nome)
        {
            try
            {
                var pasta = System.IO.Path.Combine(Settings.PastaSaida, "diagnostico");
                System.IO.Directory.CreateDirectory(pasta);
                var arquivo = System.IO.Path.Combine(pasta, $"{DateTime.Now:yyyyMMdd_HHmmss}_{nome}.png");
                await frame.Page.ScreenshotAsync(new PageScreenshotOptions { Path = arquivo });
                Log.Detalhe($"🩺 Print salvo: {arquivo}");
            }
            catch (Exception ex)
            {
                Log.Detalhe($"Não foi possível salvar o print: {ex.Message.Split('\n')[0]}");
            }
        }

        public async Task<IFrame> LocalizarFrameEventos()
        {
            Log.Detalhe("\n=== LOCALIZANDO NÚMEROS DE EVENTO ===");
            Log.Detalhe($"Aguardando a tabela carregar (até {Settings.DelayTabela / 1000}s)...");

            // Em vez de esperar um tempo fixo, verifica a cada 3s se a coluna "Nº evento" já apareceu.
            var limite = DateTime.Now.AddMilliseconds(Settings.DelayTabela);
            while (DateTime.Now < limite)
            {
                foreach (var frame in _page.Frames)
                {
                    if (frame == null || frame.Url == null) continue;

                    try
                    {
                        // Primeiro pelo ID conhecido; se o SAP mudar o ID, procura pelo texto do cabeçalho
                        var headerElement = await frame.QuerySelectorAsync(Settings.SeletorEventoHeader);
                        var texto = headerElement != null ? (await headerElement.TextContentAsync())?.Trim() : null;
                        bool achou = texto == Settings.TextoEvento ||
                                     await frame.Locator("th span").Filter(new LocatorFilterOptions { HasTextString = Settings.TextoEvento }).CountAsync() > 0;

                        // Achou a tabela; a espera pela atualização terminar fica em SelecionarConsultaPublicado
                        if (achou && await TabelaPronta(frame))
                        {
                            Log.Detalhe($"✅ Coluna 'Nº evento' encontrada.");
                            _frameEncontrado = frame;
                            await SelecionarConsultaPublicado(frame);
                            return _frameEncontrado;
                        }
                    }
                    catch
                    {
                        // frame descartado durante o carregamento: tenta de novo na próxima volta
                    }
                }
                await Task.Delay(3000);
            }

            return null;
        }

        public async Task<string> ProximoNumeroNaoExtraido(IFrame frame, HashSet<string> baseNumeros)
        {
            if (_fimTabela)
            {
                return null;
            }

            try
            {
                // Insiste um pouco mais: até MaxTentativasSemNovos buscas consecutivas sem achar número novo.
                if (_frameEncontrado != null) frame = _frameEncontrado;
                bool relocalizou = false;

                for (int tentativa = 1; tentativa <= Settings.MaxTentativasSemNovos; tentativa++)
                {
                    var numerosVisiveis = await ExtrairNumerosVisiveis(frame);
                    if (numerosVisiveis == null)
                    {
                        // A tabela não respondeu: tenta localizá-la de novo uma vez
                        if (relocalizou || (frame = await LocalizarFrameEventos()) == null)
                        {
                            TabelaIndisponivel = true;
                            Log.Erro("   ❌ A tabela de eventos ficou indisponível. Encerrando a busca (NÃO significa que não há cotações novas).");
                            return null;
                        }
                        relocalizou = true;
                        tentativa--;
                        continue;
                    }

                    var escolhido = await SelecionarCandidato(numerosVisiveis, baseNumeros);

                    if (escolhido != null)
                    {
                        _numerosJaExtraidos.Add(escolhido);
                        _tentativasSemNovos = 0; // achou: zera o contador
                        return escolhido;
                    }

                    // Não achou nenhum novo nesta passada: rola para baixo e aguarda
                    _tentativasSemNovos++;
                    Log.Detalhe($"   🔄 Tentativa {tentativa}/{Settings.MaxTentativasSemNovos} sem número novo. Rolando para procurar mais...");
                    await RealizarScroll(frame);
                    await Task.Delay(Settings.DelayPadrao);
                }

                // Esgotou as tentativas de insistência: considera o fim da tabela.
                _fimTabela = true;
                Log.Detalhe($"   ✅ Nenhum número novo após {Settings.MaxTentativasSemNovos} tentativas. Fim da tabela.");
                return null;
            }
            catch (Exception ex)
            {
                TabelaIndisponivel = true;
                Log.Erro($"   ❌ Erro ao buscar próximo número: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Seleciona o primeiro número que não está na base nem já foi extraído,
        /// ignorando eventos que começam com "60" e eventos com status ignorável.
        /// </summary>
        private async Task<string> SelecionarCandidato(List<string> numerosVisiveis, HashSet<string> baseNumeros)
        {
            foreach (var numero in numerosVisiveis)
            {
                if (_numerosJaExtraidos.Contains(numero) || baseNumeros.Contains(numero))
                {
                    continue;
                }

                // Pula eventos que começam com "60"
                if (numero.StartsWith("60", StringComparison.Ordinal))
                {
                    Log.Detalhe($"   ⏭️ Evento {numero} ignorado (começa com '60').");
                    _numerosJaExtraidos.Add(numero);
                    continue;
                }

                if (_verificarIgnorar != null)
                {
                    bool ignorar = await _verificarIgnorar(_frameEncontrado, numero);
                    if (ignorar)
                    {
                        Log.Detalhe($"   ⏭️ Evento {numero} ignorado (status Encerrado/não identificado).");
                        _numerosJaExtraidos.Add(numero);
                        continue;
                    }
                }

                return numero;
            }

            return null;
        }

        /// <summary>Números de evento visíveis na tabela. Retorna null se a tabela não puder ser lida.</summary>
        private async Task<List<string>> ExtrairNumerosVisiveis(IFrame frame)
        {
            var numeros = new List<string>();
            if (frame == null || frame.IsDetached) return null;
            try
            {
                var spans = await frame.QuerySelectorAllAsync("span.lsLink__text");
                
                foreach (var span in spans)
                {
                    try
                    {
                        var textoNumero = await span.TextContentAsync();
                        if (!string.IsNullOrEmpty(textoNumero))
                        {
                            var numero = textoNumero.Trim();
                            if (Regex.IsMatch(numero, @"^\d{10}$"))
                            {
                                numeros.Add(numero);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Detalhe($"   Erro ao extrair números: {ex.Message}");
                return null;
            }
            return numeros;
        }

        private async Task RealizarScroll(IFrame frame)
        {
            try
            {
                var scrollButton = await frame.QuerySelectorAsync(Settings.SeletorScroll);
                
                if (scrollButton != null)
                {
                    bool isVisible = await scrollButton.IsVisibleAsync();
                    if (isVisible)
                    {
                        Log.Detalhe($"   🔽 Clicando no botão de scroll para baixo...");
                        await scrollButton.ClickAsync();
                        await Task.Delay(Settings.DelayPadrao);
                        return;
                    }
                }
                
                await frame.EvaluateAsync(@"
                    () => {
                        const table = document.querySelector('table.urST3BdBrd');
                        if (!table) return;
                        
                        let parent = table.parentElement;
                        for (let i = 0; i < 20 && parent; i++) {
                            const style = window.getComputedStyle(parent);
                            if (style.overflow === 'auto' || style.overflow === 'scroll' || 
                                style.overflowY === 'auto' || style.overflowY === 'scroll') {
                                parent.scrollTop += 500;
                                return;
                            }
                            parent = parent.parentElement;
                        }
                        
                        window.scrollBy(0, 400);
                    }
                ");
                
                await Task.Delay(Settings.DelayPadrao);
            }
            catch (Exception ex)
            {
                Log.Aviso($"   ⚠️ Erro no scroll: {ex.Message}");
            }
        }

        public void ResetarExtracao()
        {
            _fimTabela = false;
            _tentativasSemNovos = 0;
            _numerosJaExtraidos.Clear();
        }
    }
}
