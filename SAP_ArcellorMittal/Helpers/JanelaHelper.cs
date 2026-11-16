using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SAP_ArcellorMittal.Helpers
{
    /// <summary>
    /// Segundo plano: a janela do navegador é levada para FORA da área visível da tela (não minimizada).
    /// Minimizar faz o Chromium tratar a página como oculta e parar de desenhá-la — o SAP fica lento e a
    /// lista "Publicado" trava. Fora da tela a página continua "visível" para o SAP e não atrapalha quem usa o PC.
    /// </summary>
    /// <summary>Ajustes para rodar em Windows antigos (o servidor de produção é Windows Server 2012 R2).</summary>
    public static class Compatibilidade
    {
        /// <summary>
        /// O Node embutido no Playwright só aceita Windows 10 / Server 2016 ou mais novo e encerra na partida
        /// ("Process exited") nos anteriores, a menos que NODE_SKIP_PLATFORM_CHECK=1 esteja definida.
        /// Chamar antes de Playwright.CreateAsync(): o driver herda a variável deste processo.
        /// </summary>
        public static void PrepararPlaywright()
        {
            if (OperatingSystem.IsWindows() && Environment.OSVersion.Version.Major < 10 &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NODE_SKIP_PLATFORM_CHECK")))
                Environment.SetEnvironmentVariable("NODE_SKIP_PLATFORM_CHECK", "1");
        }
    }

    public static class JanelaHelper
    {
        private const int ForaDaTela = -32000;

        /// <summary>
        /// Script injetado em todas as páginas: o SAP sempre enxerga a página como visível
        /// (document.hidden = false), mesmo atrás de outras janelas.
        /// </summary>
        public const string ScriptPaginaSempreVisivel = @"
            try {
                Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
                Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
                document.addEventListener('visibilitychange', e => e.stopImmediatePropagation(), true);
            } catch (e) { }";

        public static Task EnviarParaSegundoPlanoAsync(IPage page) =>
            DefinirAsync(page, new Dictionary<string, object> { ["windowState"] = "normal" },
                               new Dictionary<string, object> { ["left"] = ForaDaTela, ["top"] = ForaDaTela });

        /// <summary>Traz a janela de volta para a tela (ex.: quando é preciso fazer login manual).</summary>
        public static async Task TrazerParaFrenteAsync(IPage page)
        {
            await DefinirAsync(page, new Dictionary<string, object> { ["windowState"] = "normal" },
                                     new Dictionary<string, object> { ["left"] = 50, ["top"] = 50, ["width"] = 1280, ["height"] = 800 });
            try { await page.BringToFrontAsync(); } catch { }
        }

        private static async Task DefinirAsync(IPage page, params Dictionary<string, object>[] etapas)
        {
            try
            {
                var cdp = await page.Context.NewCDPSessionAsync(page);
                var janela = await cdp.SendAsync("Browser.getWindowForTarget");
                var id = janela.Value.GetProperty("windowId").GetInt32();
                // estado e posição vão em chamadas separadas (o Chromium não aceita os dois juntos)
                foreach (var bounds in etapas)
                    await cdp.SendAsync("Browser.setWindowBounds", new Dictionary<string, object> { ["windowId"] = id, ["bounds"] = bounds });
                await cdp.DetachAsync();
            }
            catch (Exception ex)
            {
                Log.Detalhe($"Não foi possível mover a janela: {ex.Message.Split('\n')[0]}");
            }
        }
    }
}
