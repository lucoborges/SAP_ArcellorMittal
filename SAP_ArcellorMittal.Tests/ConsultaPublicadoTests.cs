using Microsoft.Playwright;
using SAP_ArcellorMittal.Services;

namespace SAP_ArcellorMittal.Tests;

/// <summary>
/// "Consultas ativas" do portal: as linhas "Licitações na Web" e "Leilões na Web" têm consultas com os mesmos
/// nomes. A consulta selecionada deixa de ser link (fica destacada). O robô tem de achar sempre a de Licitações.
/// </summary>
public class ConsultaPublicadoTests
{
    private static string Html(bool publicadoSelecionado) => $@"<html><body><table>
  <tr><td><span>Licitações na Web</span></td>
      <td><a href='#'><span class='lsLink__text'>Todos(as) (1307)</span></a>
          {(publicadoSelecionado
              ? "<span class='selecionado'>Publicado (18)</span>"
              : "<a href='#'><span class='lsLink__text'>Publicado (18)</span></a>")}
          <a href='#'><span class='lsLink__text'>Encerrado (533)</span></a></td></tr>
  <tr><td><span>Leilões na Web</span></td>
      <td><a href='#'><span class='lsLink__text'>Todos(as) (0)</span></a>
          <a href='#'><span class='lsLink__text'>Publicado (0)</span></a></td></tr>
</table></body></html>";

    private static async Task<(string Texto, int Quantidade)> Localizar(bool selecionado, string padrao)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(Html(selecionado));
        var (link, texto, quantidade) = await ExtracaoService.LocalizarConsulta(page.MainFrame, padrao);
        Assert.NotNull(link);
        Assert.Equal(1, await link.CountAsync());
        return (texto, quantidade);
    }

    [Fact]
    public async Task Antes_do_clique_acha_o_Publicado_de_Licitacoes()
    {
        var (texto, quantidade) = await Localizar(false, @"^\s*Publicado\s*\(\d+\)\s*$");
        Assert.Equal("Publicado (18)", texto);
        Assert.Equal(18, quantidade);
    }

    [Fact]
    public async Task Depois_do_clique_nao_confunde_com_o_Publicado_de_Leiloes()
    {
        var (texto, quantidade) = await Localizar(true, @"^\s*Publicado\s*\(\d+\)\s*$");
        Assert.Equal("Publicado (18)", texto);
        Assert.Equal(18, quantidade);
    }

    [Fact]
    public async Task Todos_de_Licitacoes()
    {
        var (texto, _) = await Localizar(true, @"^\s*Todos.*\(\d+\)\s*$");
        Assert.Equal("Todos(as) (1307)", texto);
    }
}
