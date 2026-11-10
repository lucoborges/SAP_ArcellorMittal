using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;

namespace SAP_ArcellorMittal.Services
{
    public class LoginService
    {
        private readonly IPage _page;

        public LoginService(IPage page)
        {
            _page = page;
        }

        /// <summary>
        /// Decide se é preciso logar: espera até ~30s aparecer o formulário de login OU o link de cotações
        /// (sessão ainda válida, ex.: ciclo seguinte do modo contínuo). Retorna true se o formulário apareceu.
        /// </summary>
        public async Task<bool> PrecisaLogin(int timeoutSegundos = 30)
        {
            var limite = DateTime.Now.AddSeconds(timeoutSegundos);
            while (DateTime.Now < limite)
            {
                foreach (var frame in _page.Frames)
                {
                    try
                    {
                        var campo = frame.Locator(Settings.SeletorUsuario);
                        if (await campo.CountAsync() > 0 && await campo.First.IsVisibleAsync()) return true;
                        if (await frame.Locator(Settings.SeletorCotacao).CountAsync() > 0 ||
                            await frame.GetByText("Solicitações de cotação e leilões", new FrameGetByTextOptions { Exact = true }).CountAsync() > 0)
                            return false;
                    }
                    catch { /* frame trocando durante o carregamento */ }
                }
                await Task.Delay(2000);
            }
            return true; // não deu para confirmar a sessão: segue o caminho do login (que trata o "já logado")
        }

        /// <summary>
        /// Login feito pela PESSOA na janela do navegador: espera o formulário de login aparecer e sumir.
        /// Nenhuma credencial é lida ou digitada pelo programa.
        /// </summary>
        public async Task<bool> AguardarLoginManual(ContaPortal conta, int timeoutMinutos = 10)
        {
            Log.Detalhe($"\n=== LOGIN MANUAL (conta {conta.Usuario}) ===");
            Log.Info($"👉 Entre com a conta {conta.Usuario}: digite usuário e senha na janela do navegador e clique em entrar (aguardando até {timeoutMinutos} min)...");

            var limite = DateTime.Now.AddMinutes(timeoutMinutos);
            bool viuFormulario = false;
            int voltasSemFormulario = 0;
            var inicio = DateTime.Now;

            while (DateTime.Now < limite)
            {
                bool formularioVisivel = false;
                foreach (var frame in _page.Frames)
                {
                    try
                    {
                        var campo = frame.Locator(Settings.SeletorUsuario);
                        if (await campo.CountAsync() > 0 && await campo.First.IsVisibleAsync()) { formularioVisivel = true; break; }
                    }
                    catch { /* frame trocando durante o login */ }
                }

                if (formularioVisivel)
                {
                    if (!viuFormulario) Log.Detalhe("   🔐 Tela de login detectada. Aguardando você entrar...");
                    viuFormulario = true;
                    voltasSemFormulario = 0;
                }
                else
                {
                    voltasSemFormulario++;
                    // Já viu o formulário e ele sumiu por ~6s → logado.
                    // Nunca viu o formulário em 30s → provavelmente já estava logado.
                    if ((viuFormulario && voltasSemFormulario >= 3) ||
                        (!viuFormulario && DateTime.Now - inicio > TimeSpan.FromSeconds(30)))
                    {
                        try { await _page.WaitForLoadStateAsync(LoadState.NetworkIdle); } catch { }
                        await Task.Delay(Settings.DelayLongo);
                        Log.Info("✅ Login concluído! Seguindo com a automação.");
                        return true;
                    }
                }

                await Task.Delay(2000);
            }

            Log.Erro("❌ Tempo esgotado esperando o login manual.");
            return false;
        }

        public async Task<bool> RealizarLogin(ContaPortal conta)
        {
            try
            {
                Log.Detalhe($"\n=== VERIFICANDO LOGIN (conta {conta.Usuario}) ===");

                if (string.IsNullOrWhiteSpace(conta.Usuario) || string.IsNullOrWhiteSpace(conta.Senha))
                {
                    Log.Erro($"❌ Senha da conta {conta.Usuario} não configurada. Defina a variável de ambiente {conta.VariavelSenha}");
                    Log.Detalhe($"   (PowerShell: setx {conta.VariavelSenha} \"sua-senha\" — depois reabra o terminal) ou use --login-manual.");
                    return false;
                }

                var user = await _page.QuerySelectorAsync(Settings.SeletorUsuario);
                if (user != null)
                {
                    await user.FillAsync(conta.Usuario);
                    Log.Detalhe("✅ Usuário preenchido!");
                }

                var password = await _page.QuerySelectorAsync(Settings.SeletorSenha);
                if (password != null)
                {
                    await password.FillAsync(conta.Senha);
                    Log.Detalhe("✅ Senha preenchida!");
                }

                var loginButton = await _page.QuerySelectorAsync(Settings.SeletorLogin);
                if (loginButton != null)
                {
                    await loginButton.ClickAsync();
                    Log.Detalhe("✅ Botão de login clicado!");
                    await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    await Task.Delay(Settings.DelayLogin);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Erro($"❌ Erro no login: {ex.Message}");
                return false;
            }
        }
    }
}
