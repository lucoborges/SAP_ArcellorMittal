using Microsoft.Playwright;

namespace SAP_ArcellorMittal.Tests;

/// <summary>
/// Navegador dos testes que precisam de uma página de verdade. Com a variável ARCELOR_NAVEGADOR definida usa
/// esse chrome.exe (no servidor de produção o Chromium do Playwright não roda); sem ela, o Chromium do Playwright.
/// </summary>
internal static class NavegadorDeTeste
{
    public static Task<IPlaywright> CriarPlaywrightAsync()
    {
        SAP_ArcellorMittal.Helpers.Compatibilidade.PrepararPlaywright();
        return Playwright.CreateAsync();
    }

    public static Task<IBrowser> AbrirAsync(IPlaywright playwright)
    {
        var executavel = Environment.GetEnvironmentVariable("ARCELOR_NAVEGADOR");
        return playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = string.IsNullOrWhiteSpace(executavel) ? null : executavel,
        });
    }
}
