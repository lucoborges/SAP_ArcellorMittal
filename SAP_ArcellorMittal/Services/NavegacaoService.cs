using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;

namespace SAP_ArcellorMittal.Services
{
    public class NavegacaoService
    {
        private readonly IPage _page;

        public NavegacaoService(IPage page)
        {
            _page = page;
        }

        /// <summary>
        /// Clica em "Solicitações de cotação e leilões". Em caso de erro (ex.: falha passageira de rede),
        /// recarrega o portal, trata o aviso de sessão ("Avanç.") e tenta de novo.
        /// </summary>
        public async Task<bool> NavegarParaCotacoes(int tentativas = 3)
        {
            Log.Detalhe("\n=== NAVEGANDO PARA COTAÇÕES ===");

            for (int tentativa = 1; tentativa <= tentativas; tentativa++)
            {
                string urlFalha = null;
                void AoFalhar(object _, IRequest r) { if (r.Frame == _page.MainFrame && r.IsNavigationRequest) urlFalha = r.Url; }
                _page.RequestFailed += AoFalhar;
                try
                {
                    var link = await LocalizarLinkCotacao();
                    if (link == null)
                    {
                        Log.Aviso("⚠️ Link 'Solicitações de cotação e leilões' não encontrado.");
                    }
                    else
                    {
                        Log.Detalhe("✅ Link 'Solicitações de cotação e leilões' encontrado!");
                        await link.ClickAsync();
                        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        await Task.Delay(Settings.DelayLongo);

                        if (urlFalha == null)
                        {
                            Log.Detalhe("✅ Navegação concluída!");
                            return true;
                        }
                        Log.Aviso($"⚠️ Falha ao carregar '{urlFalha}'.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Aviso($"⚠️ Erro na navegação (tentativa {tentativa}/{tentativas}): {ex.Message.Split('\n')[0]}" +
                                      (urlFalha != null ? $" — endereço: {urlFalha}" : ""));
                }
                finally
                {
                    _page.RequestFailed -= AoFalhar;
                }

                if (tentativa < tentativas)
                {
                    Log.Detalhe("↻ Recarregando o portal e tentando de novo...");
                    try
                    {
                        await Task.Delay(Settings.DelayLongo);
                        await _page.GotoAsync(Settings.UrlPortal);
                        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                        await Task.Delay(Settings.DelayPadrao);
                        await VerificadorHelper.VerificarBotaoAvanc(_page);
                    }
                    catch (Exception ex)
                    {
                        Log.Aviso($"⚠️ Erro ao recarregar o portal: {ex.Message.Split('\n')[0]}");
                    }
                }
            }

            Log.Erro("❌ Não foi possível abrir 'Solicitações de cotação e leilões'.");
            return false;
        }

        private async Task<ILocator> LocalizarLinkCotacao()
        {
            foreach (var frame in _page.Frames)
            {
                try
                {
                    var porId = frame.Locator(Settings.SeletorCotacao);
                    if (await porId.CountAsync() > 0) return porId.First;

                    var porTexto = frame.GetByText("Solicitações de cotação e leilões", new FrameGetByTextOptions { Exact = true });
                    if (await porTexto.CountAsync() > 0) return porTexto.First;
                }
                catch { /* frame em transição */ }
            }
            return null;
        }
    }
}
