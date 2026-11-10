using Microsoft.Playwright;
using SAP_ArcellorMittal.Services;

namespace SAP_ArcellorMittal.Tests;

/// <summary>
/// Leitura das colunas da linha do evento numa página que imita a lista do SAP: cabeçalho de &lt;th&gt; no mesmo
/// corpo da tabela, 1ª célula de seleção em &lt;th&gt; e o link dentro de uma tabela interna.
/// </summary>
public class CapturaDadosPortalTests
{
    private const string Html = @"<html><body>
<table class='urST3BdBrd'><tbody>
  <tr><th>&nbsp;</th><th><div>Nº evento</div></th><th>Descrição de eventos</th><th>Status do evento</th><th>Data final</th><th>Nº cotação</th></tr>
  <tr><th><div></div></th>
      <td><table><tbody><tr><td><a href='#'><span class='lsLink__text'>7000000343</span></a></td></tr></tbody></table></td>
      <td>SENSOR DE NIVEL TESTE</td><td>Publicado</td><td>12.11.2026</td><td></td></tr>
</tbody></table></body></html>";

    [Fact]
    public async Task Le_as_colunas_da_linha_do_evento_pelos_titulos()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(Html);

        var dados = await ProcessamentoService.CapturarDadosPortal(page.MainFrame, "7000000343");

        Assert.Equal("7000000343", dados["Nº evento"]);
        Assert.Equal("SENSOR DE NIVEL TESTE", dados["Descrição de eventos"]);
        Assert.Equal("Publicado", dados["Status do evento"]);
        Assert.Equal("12.11.2026", dados["Data final"]);
        Assert.Equal("", dados["Nº cotação"]);
    }

    [Fact]
    public async Task Evento_que_nao_esta_na_tabela_devolve_vazio()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(Html);

        Assert.Empty(await ProcessamentoService.CapturarDadosPortal(page.MainFrame, "7000000000"));
    }
}
