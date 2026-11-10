using System.Linq;
using System.Text.RegularExpressions;
using SAP_ArcellorMittal.Services.Pdf;

namespace SAP_ArcellorMittal.Tests;

/// <summary>Testes com linhas sintéticas, para cenários que ainda não temos em PDF real.</summary>
public class PdfCotacaoParserCenariosTests
{
    private const double Margem = 536.6;
    private const double Car = PalavraPdf.LarguraMediaCaractere; // 4.4 pt por caractere
    private const double Espaco = 2.2;

    private static LinhaPdf L(int pagina, double y, params (string texto, double x)[] palavras) => new()
    {
        Pagina = pagina,
        Y = y,
        Palavras = palavras.Select(p => new PalavraPdf(p.texto, p.x)).ToList()
    };

    /// <summary>Uma linha com as palavras posicionadas como no PDF (largura por caractere + espaços reais).</summary>
    private static LinhaPdf T(int pagina, double y, double x, string texto)
    {
        var palavras = new List<PalavraPdf>();
        foreach (Match m in Regex.Matches(texto, @"(\S+)( *)"))
        {
            palavras.Add(new PalavraPdf(m.Groups[1].Value, x));
            x += m.Groups[1].Length * Car + m.Groups[2].Length * Espaco;
        }
        return new LinhaPdf { Pagina = pagina, Y = y, Palavras = palavras };
    }

    /// <summary>
    /// Simula o formulário do SAP: cada linha SAP começa numa linha visual nova e é quebrada
    /// num espaço quando a próxima palavra não cabe até a margem direita.
    /// </summary>
    private static List<LinhaPdf> Renderizar(IEnumerable<string> linhasSap, ref double y, int pagina = 1, double x0 = 44)
    {
        var resultado = new List<LinhaPdf>();
        foreach (var sap in linhasSap)
        {
            var atual = new List<PalavraPdf>();
            double x = x0;
            foreach (Match m in Regex.Matches(sap, @"(\S+)( *)"))
            {
                var w = m.Groups[1].Value;
                if (atual.Count > 0 && x + w.Length * Car > Margem)
                {
                    resultado.Add(new LinhaPdf { Pagina = pagina, Y = y, Palavras = atual });
                    y -= 10;
                    atual = new List<PalavraPdf>();
                    x = x0;
                }
                atual.Add(new PalavraPdf(w, x));
                x += w.Length * Car + m.Groups[2].Length * Espaco;
            }
            resultado.Add(new LinhaPdf { Pagina = pagina, Y = y, Palavras = atual });
            y -= 10;
        }
        return resultado;
    }

    /// <summary>Corta um parágrafo em linhas de 132, como o SAP grava (TDLINE), no meio da palavra.</summary>
    private static List<string> CortarSap(string paragrafo)
    {
        var linhas = new List<string>();
        for (int i = 0; i < paragrafo.Length; i += 132)
            linhas.Add(paragrafo.Substring(i, Math.Min(132, paragrafo.Length - i)));
        // Pré-condição do cenário: nenhum corte cai num espaço (senão o SAP guardaria espaço no início/fim)
        for (int i = 132; i < paragrafo.Length; i += 132)
            Assert.True(paragrafo[i] != ' ' && paragrafo[i - 1] != ' ', $"corte na posição {i} cai em espaço; ajuste o texto do teste");
        return linhas;
    }

    private static List<LinhaPdf> CabecalhoPadrao(int pagina = 1) => new()
    {
        T(pagina, 680, 314, "Número da solicitação de cotação: 7000000001"),
        L(pagina, 495, ("Item", 44), ("Categoria", 115), ("de", 154), ("item", 165), ("Aplicação", 228), ("Data", 370), ("de", 389), ("remessa", 401)),
        L(pagina, 481, ("Quantidade", 44), ("Número", 115), ("do", 147), ("produto", 159), ("Denominação", 228), ("Centro", 370)),
        new LinhaPdf { Pagina = pagina, Y = 467, Palavras = { new PalavraPdf(new string('-', 148), 43.8, Margem) } },
    };

    private static List<LinhaPdf> ItemComTexto(IEnumerable<string> linhasSap, out string esperado)
    {
        var linhas = CabecalhoPadrao();
        linhas.Add(L(1, 452, ("1", 44), ("Material", 115), ("Consumo", 228), ("23.01.2027", 370)));
        linhas.Add(L(1, 438, ("1", 44), ("UN", 53), ("123", 115), ("SENSOR", 228), ("AMT", 370)));
        double y = 412;
        var sap = linhasSap.ToList();
        linhas.AddRange(Renderizar(sap, ref y));
        esperado = PdfCotacaoParser.JuntarLinhasSap(sap);
        return linhas;
    }

    private const string ParagrafoLongo =
        "TERMOPARXXXX ; CONSTRUCAO: FLANGE INOX-310-DN4 ; TIPO: SENSOR S-SIMPLES ; BITOLA: 27 AWG/0,35 MM ; MATERIAL SENSOR: PTRHPT 10% IEC 584 " +
        "CLASSE 2 ; SECAO NOMINAL: 0,35MM ; JUNTA DE MEDICAO: SOLDADA ; ISOLACAO: ALSINT C799 D. 8,5MM ; PROTECAO: ALSINT C799 D. 15 X 10MM ; " +
        "LIGACAO: PARAF M4 C/ C COMP R/S 2X1MM2 ; CABECOTE: POLIESTER 80X75X55MM ; FIM";

    [Fact]
    public void Paragrafo_curto_seguido_de_linhas_cheias_nao_desalinha_o_texto()
    {
        // Cenário que quebrava a versão por contagem de caracteres: "OBS:" + linhas de 132 cortadas no meio da palavra
        var sap = new List<string> { "OBS: VER DESENHO" };
        sap.AddRange(CortarSap(ParagrafoLongo));
        sap.Add("NOTA: X");
        sap.AddRange(CortarSap(ParagrafoLongo));

        var c = PdfCotacaoParser.Interpretar(ItemComTexto(sap, out var esperado));

        Assert.Equal("OBS: VER DESENHO " + ParagrafoLongo + " NOTA: X " + ParagrafoLongo, esperado); // sanidade do teste
        Assert.Equal(esperado, Assert.Single(c.Itens).TextoLongoOriginal);
    }

    [Fact]
    public void Espacos_duplos_dentro_da_linha_SAP_sao_preservados_na_medicao()
    {
        var semDuplo = ParagrafoLongo.Replace("TERMOPARXXXX", "TERMOPARXXX");
        var paragrafo = semDuplo.Replace("TIPO: SENSOR", "TIPO:  SENSOR"); // espaço duplo na 1ª linha SAP
        var sap = CortarSap(paragrafo);

        var c = PdfCotacaoParser.Interpretar(ItemComTexto(sap, out _));

        var item = Assert.Single(c.Itens);
        Assert.Equal(paragrafo, item.TextoLongoOriginal);
        Assert.Equal(semDuplo, item.TextoLongo); // versão limpa normaliza o espaço duplo
    }

    [Fact]
    public void Linha_do_texto_que_comeca_com_rotulo_do_cabecalho_nao_e_descartada()
    {
        var sap = new List<string> { "TIPO: SENSOR ;", "DESCRIÇÃO: TERMOPAR TIPO K ;", "VERSÃO: 2 ;", "TELEFONE: N/A ;", "NOTA: X" };

        var c = PdfCotacaoParser.Interpretar(ItemComTexto(sap, out _));

        Assert.Equal("TIPO: SENSOR ; DESCRIÇÃO: TERMOPAR TIPO K ; VERSÃO: 2 ; TELEFONE: N/A ; NOTA: X", Assert.Single(c.Itens).TextoLongo);
    }

    [Fact]
    public void Le_varios_itens_inclusive_item_sem_texto_longo()
    {
        var linhas = CabecalhoPadrao();
        linhas.AddRange(new[]
        {
            L(1, 452, ("1", 44), ("Material", 115), ("Consumo", 228), ("23.01.2027", 370)),
            L(1, 438, ("1.000", 44), ("KG", 70), ("123", 115), ("PARAFUSO", 228), ("AMT-ArcelorMittal", 370), ("Tubarão", 436)),
            T(1, 412, 58, "PARAFUSO SEXTAVADO ; NOTA X"),
            L(1, 390, ("2", 44), ("Serviço", 115), ("Consumo", 228), ("24.01.2027", 370)),
            L(1, 376, ("2,5", 44), ("H", 60), ("456", 115), ("MANUTENÇÃO", 228), ("AMT-ArcelorMittal", 370), ("Tubarão", 436)),
            L(1, 360, ("3", 44), ("Material", 115), ("Consumo", 228), ("25.01.2027", 370)),
            L(1, 346, ("5", 44), ("PC", 53), ("789", 115), ("VÁLVULA", 228), ("AMV-ArcelorMittal", 370), ("Vega", 436)),
            T(1, 334, 228, "ESFERA 2POL"),
            T(1, 320, 58, "VÁLVULA ESFERA ; DN 50"),
        });

        var c = PdfCotacaoParser.Interpretar(linhas);

        Assert.Equal(3, c.Itens.Count);
        Assert.Equal(1000m, c.Itens[0].Quantidade);
        Assert.Equal("KG", c.Itens[0].Unidade);
        Assert.Equal("PARAFUSO SEXTAVADO ; NOTA X", c.Itens[0].TextoLongo);

        Assert.Equal("Serviço", c.Itens[1].Categoria);
        Assert.Equal(2.5m, c.Itens[1].Quantidade);
        Assert.Equal("H", c.Itens[1].Unidade);
        Assert.Null(c.Itens[1].TextoLongo);

        Assert.Equal("VÁLVULA ESFERA 2POL", c.Itens[2].Denominacao);
        Assert.Equal("AMV-ArcelorMittal Vega", c.Itens[2].Centro);
        Assert.Equal("VÁLVULA ESFERA ; DN 50", c.Itens[2].TextoLongo);
    }

    [Fact]
    public void Texto_longo_continua_na_pagina_seguinte_ignorando_cabecalho_repetido()
    {
        var linhas = CabecalhoPadrao();
        linhas.AddRange(new[]
        {
            L(1, 452, ("1", 44), ("Material", 115), ("Consumo", 228), ("23.01.2027", 370)),
            L(1, 438, ("1", 44), ("UN", 53), ("123", 115), ("SENSOR", 228), ("AMT", 370)),
            T(1, 60, 58, "PRIMEIRA PARTE ;"),
            T(1, 30, 44, "Página 1 de 2"),
        });
        // página 2 repete título, bloco "Informação" e cabeçalhos da tabela
        linhas.Add(T(2, 739, 321, "Solicitação de cotação"));
        linhas.Add(T(2, 692, 314, "Descrição: OUTRA COISA"));
        linhas.AddRange(CabecalhoPadrao(2));
        linhas.Add(T(2, 452, 44, "SEGUNDA PARTE"));
        linhas.Add(T(2, 30, 44, "Página 2 de 2"));

        var c = PdfCotacaoParser.Interpretar(linhas);

        var item = Assert.Single(c.Itens);
        Assert.Equal("PRIMEIRA PARTE ; SEGUNDA PARTE", item.TextoLongo);
        Assert.Empty(c.Avisos);
    }

    [Fact]
    public void Conteudo_depois_de_um_salto_grande_nao_entra_no_texto_do_item()
    {
        var linhas = CabecalhoPadrao();
        linhas.AddRange(new[]
        {
            L(1, 452, ("1", 44), ("Material", 115), ("Consumo", 228), ("23.01.2027", 370)),
            L(1, 438, ("1", 44), ("UN", 53), ("123", 115), ("SENSOR", 228), ("AMT", 370)),
            T(1, 412, 58, "TEXTO DO ITEM"),
            T(1, 300, 44, "Condições gerais de fornecimento"),
        });

        var c = PdfCotacaoParser.Interpretar(linhas);

        Assert.Equal("TEXTO DO ITEM", Assert.Single(c.Itens).TextoLongo);
        Assert.Contains(c.Avisos, a => a.Contains("Condições gerais"));
    }

    [Fact]
    public void JuntarLinhasSap_une_sem_espaco_so_quando_a_linha_anterior_estava_cheia()
    {
        Assert.Equal("ABCDEFGHIJKL MN", PdfCotacaoParser.JuntarLinhasSap(new[] { "ABCDEFGHIJ", "KL", "MN" }, largura: 10));
        Assert.Equal("", PdfCotacaoParser.JuntarLinhasSap(Array.Empty<string>()));
    }
}
