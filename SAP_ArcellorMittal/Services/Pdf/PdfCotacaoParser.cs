using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SAP_ArcellorMittal.Models;
using UglyToad.PdfPig;

namespace SAP_ArcellorMittal.Services.Pdf
{
    /// <summary>
    /// Uma palavra do PDF com sua posição horizontal (em pontos).
    /// <paramref name="XFimInformado"/> é a borda direita real; quando ausente (testes sintéticos) é estimada.
    /// </summary>
    public record PalavraPdf(string Texto, double X, double? XFimInformado = null)
    {
        /// <summary>Largura média de um caractere em Helvetica 8 (usada só quando a borda direita não é conhecida).</summary>
        public const double LarguraMediaCaractere = 4.4;

        public double XFim => XFimInformado ?? X + Texto.Length * LarguraMediaCaractere;
        public double Largura => XFim - X;
    }

    /// <summary>Uma linha visual do PDF: palavras com a mesma altura, ordenadas da esquerda para a direita.</summary>
    public class LinhaPdf
    {
        public int Pagina { get; init; }
        /// <summary>Posição vertical (base da linha). No PDF o Y cresce de baixo para cima.</summary>
        public double Y { get; init; }
        public List<PalavraPdf> Palavras { get; init; } = new List<PalavraPdf>();

        public double X => Palavras.Count > 0 ? Palavras[0].X : 0;
        public double XFim => Palavras.Count > 0 ? Palavras[^1].XFim : 0;

        /// <summary>Texto com um espaço entre palavras (para reconhecer rótulos e colunas).</summary>
        public string Texto => string.Join(" ", Palavras.Select(p => p.Texto));

        /// <summary>
        /// Texto preservando espaços repetidos: o nº de espaços entre duas palavras é calculado pela distância entre elas.
        /// Necessário para medir as linhas do SAP (132 caracteres) corretamente.
        /// </summary>
        public string TextoComEspacos(double larguraEspaco)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Palavras.Count; i++)
            {
                if (i > 0)
                {
                    var vao = Palavras[i].X - Palavras[i - 1].XFim;
                    int espacos = larguraEspaco > 0 ? (int)Math.Round(vao / larguraEspaco) : 1;
                    sb.Append(' ', Math.Max(1, espacos));
                }
                sb.Append(Palavras[i].Texto);
            }
            return sb.ToString();
        }

        public override string ToString() => $"p{Pagina} y={Y:F0} x={X:F0} | {Texto}";
    }

    /// <summary>
    /// Lê o PDF "Solicitação de cotação" gerado pelo botão "Visualização" do SAP SRM da ArcelorMittal.
    ///
    /// Layout esperado (posições aproximadas em pontos):
    ///   Item      Categoria de item   Aplicação     Data de remessa
    ///   Quantidade Número do produto  Denominação   Centro
    ///   ------------------------------------------------------------
    ///   1         Material            Consumo       23.01.2027
    ///   18 UN     11374588            AJUSTADOR...  AMT-ArcelorMittal Tubarão
    ///                                 (continuação da denominação)
    ///      TEXTO LONGO DO ITEM ... (linhas de 132 caracteres do SAP, quebradas visualmente)
    /// </summary>
    public static class PdfCotacaoParser
    {
        /// <summary>Largura da linha de texto do SAP (TDLINE). O texto longo é gravado em linhas de 132 caracteres.</summary>
        public const int LarguraLinhaSap = 132;

        private const double ToleranciaColuna = 6;
        private const double ToleranciaMesmaLinha = 2;
        /// <summary>Espaço vertical (pontos) acima do qual consideramos que o texto longo do item acabou.</summary>
        private const double SaltoFimTexto = 30;

        private static readonly CultureInfo PtBr = new CultureInfo("pt-BR");
        private static readonly Regex RegexData = new Regex(@"^\d{2}\.\d{2}\.\d{4}$");
        private static readonly Regex RegexInteiro = new Regex(@"^\d+$");
        private static readonly Regex RegexQuantidade = new Regex(@"^\d{1,3}(\.\d{3})*(,\d+)?$|^\d+(,\d+)?$");
        private static readonly Regex RegexTracos = new Regex(@"^-{20,}$");
        private static readonly Regex RegexRodapePagina = new Regex(@"^(P[áa]g(ina)?\.?)\s*\d+(\s*(de|/)\s*\d+)?$", RegexOptions.IgnoreCase);

        public static Cotacao LerArquivo(string caminhoPdf)
        {
            var linhas = ExtrairLinhas(caminhoPdf);
            var cotacao = Interpretar(linhas);
            cotacao.ArquivoPdf = caminhoPdf;
            return cotacao;
        }

        public static Cotacao LerBytes(byte[] pdf)
        {
            return Interpretar(ExtrairLinhas(pdf));
        }

        public static List<LinhaPdf> ExtrairLinhas(string caminhoPdf)
        {
            using var doc = PdfDocument.Open(caminhoPdf);
            return ExtrairLinhas(doc);
        }

        public static List<LinhaPdf> ExtrairLinhas(byte[] pdf)
        {
            using var doc = PdfDocument.Open(pdf);
            return ExtrairLinhas(doc);
        }

        private static List<LinhaPdf> ExtrairLinhas(PdfDocument doc)
        {
            var resultado = new List<LinhaPdf>();
            foreach (var pagina in doc.GetPages())
            {
                var palavras = pagina.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .OrderByDescending(w => w.BoundingBox.Bottom)
                    .ThenBy(w => w.BoundingBox.Left)
                    .ToList();

                var grupos = new List<List<UglyToad.PdfPig.Content.Word>>();
                foreach (var palavra in palavras)
                {
                    var ultimo = grupos.LastOrDefault();
                    if (ultimo != null && Math.Abs(ultimo[0].BoundingBox.Bottom - palavra.BoundingBox.Bottom) <= ToleranciaMesmaLinha)
                        ultimo.Add(palavra);
                    else
                        grupos.Add(new List<UglyToad.PdfPig.Content.Word> { palavra });
                }

                foreach (var grupo in grupos)
                {
                    resultado.Add(new LinhaPdf
                    {
                        Pagina = pagina.Number,
                        Y = grupo[0].BoundingBox.Bottom,
                        Palavras = grupo.OrderBy(w => w.BoundingBox.Left)
                                        .Select(w => new PalavraPdf(w.Text, w.BoundingBox.Left, w.BoundingBox.Right))
                                        .ToList()
                    });
                }
            }
            return resultado;
        }

        /// <summary>Interpreta as linhas visuais do PDF. Público para permitir testes com linhas sintéticas.</summary>
        public static Cotacao Interpretar(IReadOnlyList<LinhaPdf> linhas)
        {
            var cotacao = new Cotacao();
            LerCabecalho(linhas, cotacao);

            var colunas = Colunas.Detectar(linhas, cotacao.Avisos);
            LerItens(linhas, colunas, cotacao);

            if (string.IsNullOrWhiteSpace(cotacao.NumeroSolicitacao))
                cotacao.Avisos.Add("Número da solicitação de cotação não encontrado no PDF.");
            if (cotacao.Itens.Count == 0)
                cotacao.Avisos.Add("Nenhum item encontrado no PDF.");

            return cotacao;
        }

        // ------------------------------------------------------------------ cabeçalho

        private static readonly (string Rotulo, Action<Cotacao, string> Atribuir)[] CamposCabecalho =
        {
            ("Número da solicitação de cotação:", (c, v) => c.NumeroSolicitacao = v),
            ("Descrição:", (c, v) => c.Descricao = v),
            ("Comprador:", (c, v) => c.Comprador = v),
            ("Telefone:", (c, v) => c.Telefone = v),
            ("E-mail:", (c, v) => c.Email = v),
            ("Versão:", (c, v) => c.Versao = v),
            ("Prazo para apresentação:", (c, v) => c.PrazoApresentacao = ConverterDataHora(v)),
            ("Data de abertura:", (c, v) => c.DataAbertura = ConverterDataHora(v)),
        };

        private static void LerCabecalho(IReadOnlyList<LinhaPdf> linhas, Cotacao cotacao)
        {
            var preenchidos = new HashSet<string>();
            foreach (var linha in linhas)
            {
                if (EhLinhaDeTracos(linha)) break; // os itens começam após a linha de traços
                var texto = linha.Texto;
                foreach (var (rotulo, atribuir) in CamposCabecalho)
                {
                    if (preenchidos.Contains(rotulo)) continue;
                    if (texto.StartsWith(rotulo, StringComparison.OrdinalIgnoreCase))
                    {
                        var valor = texto.Substring(rotulo.Length).Trim();
                        atribuir(cotacao, valor.Length == 0 ? null : valor);
                        preenchidos.Add(rotulo);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Títulos que podem se repetir no topo de outras páginas. Comparação EXATA e sensível a maiúsculas:
        /// o texto longo dos itens é todo em maiúsculas, então não há risco de descartar linhas do item.
        /// </summary>
        private static bool EhTituloRepetido(string texto) =>
            texto == "Informação" || texto == "Solicitação de cotação" ||
            texto == "Detalhes da solicitação de cotação" || texto == "Prazos da solicitação de cotação:";

        // ------------------------------------------------------------------ colunas

        private sealed class Colunas
        {
            public double Item = 44, Categoria = 115, Aplicacao = 228, Data = 370;
            public double Quantidade = 44, Produto = 115, Denominacao = 228, Centro = 370;
            /// <summary>Margem direita da área de texto (= fim da linha de traços).</summary>
            public double MargemDireita = 536.6;

            public static Colunas Detectar(IReadOnlyList<LinhaPdf> linhas, List<string> avisos)
            {
                var c = new Colunas();
                var cab1 = linhas.FirstOrDefault(EhCabecalhoItem);
                var cab2 = linhas.FirstOrDefault(EhCabecalhoQuantidade);
                var tracos = linhas.FirstOrDefault(EhLinhaDeTracos);
                if (tracos != null) c.MargemDireita = tracos.XFim;

                if (cab1 != null)
                {
                    c.Item = PosicaoDe(cab1, "Item") ?? c.Item;
                    c.Categoria = PosicaoDe(cab1, "Categoria") ?? c.Categoria;
                    c.Aplicacao = PosicaoDe(cab1, "Aplicação") ?? c.Aplicacao;
                    c.Data = PosicaoDe(cab1, "Data") ?? c.Data;
                }
                else avisos.Add("Cabeçalho 'Item / Categoria de item' não encontrado; usando posições padrão das colunas.");

                if (cab2 != null)
                {
                    c.Quantidade = PosicaoDe(cab2, "Quantidade") ?? c.Quantidade;
                    c.Produto = PosicaoDe(cab2, "Número") ?? c.Produto;
                    c.Denominacao = PosicaoDe(cab2, "Denominação") ?? c.Denominacao;
                    c.Centro = PosicaoDe(cab2, "Centro") ?? c.Centro;
                }
                else avisos.Add("Cabeçalho 'Quantidade / Número do produto' não encontrado; usando posições padrão das colunas.");

                return c;
            }

            private static double? PosicaoDe(LinhaPdf linha, string palavra) =>
                linha.Palavras.FirstOrDefault(p => p.Texto.Equals(palavra, StringComparison.OrdinalIgnoreCase))?.X;
        }

        private static bool EhCabecalhoItem(LinhaPdf l) =>
            l.Texto.StartsWith("Item Categoria de item", StringComparison.Ordinal);

        private static bool EhCabecalhoQuantidade(LinhaPdf l) =>
            l.Texto.StartsWith("Quantidade Número do produto", StringComparison.Ordinal);

        private static bool EhLinhaDeTracos(LinhaPdf l) =>
            l.Palavras.Count == 1 && RegexTracos.IsMatch(l.Palavras[0].Texto);

        private static bool Perto(double x, double coluna) => Math.Abs(x - coluna) <= ToleranciaColuna;

        // ------------------------------------------------------------------ itens

        private static bool EhLinhaIgnoravel(LinhaPdf l)
        {
            var t = l.Texto;
            return EhLinhaDeTracos(l) || EhCabecalhoItem(l) || EhCabecalhoQuantidade(l) ||
                   RegexRodapePagina.IsMatch(t) || EhTituloRepetido(t);
        }

        /// <summary>
        /// Linhas do corpo (itens). Em cada página que tiver a linha de traços, descarta tudo acima dela
        /// (cabeçalho da cotação na 1ª página, cabeçalho repetido nas demais). Depois remove títulos,
        /// cabeçalhos de coluna e rodapés de página reconhecidos por texto exato.
        /// </summary>
        private static List<LinhaPdf> LinhasDoCorpo(IReadOnlyList<LinhaPdf> linhas, Cotacao cotacao)
        {
            if (!linhas.Any(EhLinhaDeTracos))
                cotacao.Avisos.Add("Linha de traços que separa o cabeçalho dos itens não encontrada.");

            var corpo = new List<LinhaPdf>();
            foreach (var pagina in linhas.GroupBy(l => l.Pagina).OrderBy(g => g.Key))
            {
                var daPagina = pagina.ToList();
                int tracos = daPagina.FindIndex(EhLinhaDeTracos); // -1 → página sem cabeçalho: usa tudo
                corpo.AddRange(daPagina.Skip(tracos + 1).Where(l => !EhLinhaIgnoravel(l)));
            }
            return corpo;
        }

        /// <summary>Largura de um espaço: o menor vão frequente entre palavras do texto longo (≈2,2 pt em Helvetica 8).</summary>
        private static double EstimarLarguraEspaco(IEnumerable<LinhaPdf> linhas)
        {
            var vaos = new List<double>();
            foreach (var l in linhas)
                for (int i = 1; i < l.Palavras.Count; i++)
                {
                    var vao = l.Palavras[i].X - l.Palavras[i - 1].XFim;
                    if (vao > 0.5) vaos.Add(vao);
                }
            if (vaos.Count == 0) return 2.2;
            vaos.Sort();
            return vaos[vaos.Count / 4]; // quartil inferior: robusto a espaços duplos
        }

        /// <summary>1ª linha do item: "1  Material  Consumo  23.01.2027".</summary>
        private static bool EhLinhaItem(LinhaPdf l, Colunas c)
        {
            if (l.Palavras.Count < 2) return false;
            var primeira = l.Palavras[0];
            var ultima = l.Palavras[^1];
            return Perto(primeira.X, c.Item) && RegexInteiro.IsMatch(primeira.Texto) &&
                   Perto(ultima.X, c.Data) && RegexData.IsMatch(ultima.Texto) &&
                   l.Palavras.Any(p => Perto(p.X, c.Categoria));
        }

        /// <summary>2ª linha do item: "18 UN  11374588  DENOMINAÇÃO  CENTRO".</summary>
        private static bool EhLinhaQuantidade(LinhaPdf l, Colunas c)
        {
            if (l.Palavras.Count < 2) return false;
            var primeira = l.Palavras[0];
            return Perto(primeira.X, c.Quantidade) && RegexQuantidade.IsMatch(primeira.Texto);
        }

        private static void LerItens(IReadOnlyList<LinhaPdf> linhas, Colunas c, Cotacao cotacao)
        {
            var corpo = LinhasDoCorpo(linhas, cotacao);
            var larguraEspaco = EstimarLarguraEspaco(corpo);

            ItemCotacao item = null;
            List<LinhaPdf> textoItem = null;
            bool textoIniciado = false;
            bool textoEncerrado = false;
            LinhaPdf anterior = null;

            void FecharItem()
            {
                if (item == null) return;
                var original = MontarTextoSap(textoItem, c.MargemDireita, larguraEspaco, c.Item);
                item.TextoLongoOriginal = original.Length == 0 ? null : original;
                item.TextoLongo = original.Length == 0 ? null : LimparTexto(original);
                cotacao.Itens.Add(item);
                item = null;
            }

            for (int i = 0; i < corpo.Count; i++)
            {
                var linha = corpo[i];

                if (EhLinhaItem(linha, c) && i + 1 < corpo.Count && EhLinhaQuantidade(corpo[i + 1], c))
                {
                    FecharItem();
                    item = LerLinhaItem(linha, c);
                    LerLinhaQuantidade(corpo[i + 1], c, item);
                    textoItem = new List<LinhaPdf>();
                    textoIniciado = false;
                    textoEncerrado = false;
                    anterior = corpo[i + 1];
                    i++;
                    continue;
                }

                if (item == null)
                {
                    cotacao.Avisos.Add($"Linha fora de qualquer item ignorada: {linha}");
                    continue;
                }

                // Antes do texto longo: linhas alinhadas à coluna Denominação/Centro são continuação desses campos
                if (!textoIniciado && linha.X >= c.Denominacao - ToleranciaColuna)
                {
                    foreach (var p in linha.Palavras)
                    {
                        if (p.X >= c.Centro - ToleranciaColuna)
                            item.Centro = Juntar(item.Centro, p.Texto);
                        else
                            item.Denominacao = Juntar(item.Denominacao, p.Texto);
                    }
                    anterior = linha;
                    continue;
                }

                // Um salto vertical grande na mesma página indica que o bloco do item terminou
                if (textoIniciado && anterior != null && anterior.Pagina == linha.Pagina &&
                    anterior.Y - linha.Y > SaltoFimTexto)
                {
                    textoEncerrado = true;
                }

                if (textoEncerrado)
                {
                    cotacao.Avisos.Add($"Item {item.NumeroItem}: linha após o texto longo ignorada: {linha}");
                    anterior = linha;
                    continue;
                }

                textoIniciado = true;
                textoItem.Add(linha);
                anterior = linha;
            }

            FecharItem();
        }

        private static ItemCotacao LerLinhaItem(LinhaPdf linha, Colunas c)
        {
            var item = new ItemCotacao { NumeroItem = linha.Palavras[0].Texto };
            var data = linha.Palavras[^1].Texto;
            item.DataRemessa = ConverterData(data);
            item.Categoria = TextoEntre(linha, c.Categoria, c.Aplicacao);
            item.Aplicacao = TextoEntre(linha, c.Aplicacao, c.Data);
            return item;
        }

        private static void LerLinhaQuantidade(LinhaPdf linha, Colunas c, ItemCotacao item)
        {
            var qtdUnidade = linha.Palavras.Where(p => p.X < c.Produto - ToleranciaColuna).Select(p => p.Texto).ToList();
            item.QuantidadeTexto = qtdUnidade.FirstOrDefault();
            item.Unidade = qtdUnidade.Count > 1 ? string.Join(" ", qtdUnidade.Skip(1)) : null;
            if (item.QuantidadeTexto != null &&
                decimal.TryParse(item.QuantidadeTexto, NumberStyles.Number, PtBr, out var qtd))
                item.Quantidade = qtd;

            item.NumeroProduto = TextoEntre(linha, c.Produto, c.Denominacao);
            item.Denominacao = TextoEntre(linha, c.Denominacao, c.Centro);
            item.Centro = TextoEntre(linha, c.Centro, double.MaxValue);
        }

        private static string TextoEntre(LinhaPdf linha, double de, double ate)
        {
            var palavras = linha.Palavras
                .Where(p => p.X >= de - ToleranciaColuna && p.X < ate - ToleranciaColuna)
                .Select(p => p.Texto);
            var texto = string.Join(" ", palavras);
            return texto.Length == 0 ? null : texto;
        }

        private static string Juntar(string atual, string mais) =>
            string.IsNullOrEmpty(atual) ? mais : atual + " " + mais;

        // ------------------------------------------------------------------ texto longo

        /// <summary>
        /// Reconstrói o texto longo do SAP a partir das linhas visuais do PDF.
        ///
        /// O SAP grava o texto em linhas de 132 caracteres cortadas no meio da palavra
        /// (ex.: "...NÃO AP" + "LICÁVEL"). O formulário PDF quebra cada linha SAP longa em
        /// duas linhas visuais, num espaço. Para separar os dois tipos de quebra usamos a GEOMETRIA:
        ///  - quebra visual (do formulário): a linha terminou porque a próxima palavra não cabia,
        ///    ou seja, fim da linha + espaço + largura da 1ª palavra seguinte ultrapassa a margem direita.
        ///    Essas linhas são unidas com espaço;
        ///  - senão é fim de linha SAP. Linhas SAP são unidas sem nada quando a anterior tinha 132
        ///    caracteres (corte no meio da palavra) e com espaço quando era mais curta (fim de parágrafo).
        /// </summary>
        public static string MontarTextoSap(IReadOnlyList<LinhaPdf> linhasVisuais, double margemDireita, double larguraEspaco,
                                            double margemEsquerda, int largura = LarguraLinhaSap)
        {
            var linhasSap = AgruparLinhasSap(linhasVisuais, margemDireita, larguraEspaco, margemEsquerda);
            return JuntarLinhasSap(linhasSap, largura);
        }

        /// <summary>Agrupa linhas visuais em linhas SAP usando a posição das palavras (ver <see cref="MontarTextoSap"/>).</summary>
        /// <remarks>
        /// Quando o corte de 132 cai logo antes de um espaço, a linha SAP seguinte começa com esse espaço;
        /// no PDF ele aparece como um recuo (x maior que a margem esquerda). Esses espaços iniciais são
        /// recolocados para que a linha meça 132 corretamente. A 1ª linha do texto é exceção: o formulário
        /// a posiciona com um recuo próprio que não corresponde a espaços.
        /// </remarks>
        public static List<string> AgruparLinhasSap(IReadOnlyList<LinhaPdf> linhasVisuais, double margemDireita, double larguraEspaco,
                                                    double margemEsquerda = double.NaN)
        {
            var linhasSap = new List<string>();
            string atual = null;
            for (int i = 0; i < linhasVisuais.Count; i++)
            {
                var visual = linhasVisuais[i];
                if (visual.Palavras.Count == 0) continue;
                var texto = visual.TextoComEspacos(larguraEspaco);
                if (atual == null)
                {
                    // início de uma linha SAP (que não é a primeira do texto): recupera espaços iniciais
                    if (linhasSap.Count > 0 && !double.IsNaN(margemEsquerda) && larguraEspaco > 0)
                    {
                        int iniciais = (int)Math.Round((visual.X - margemEsquerda) / larguraEspaco);
                        if (iniciais > 0 && iniciais <= 10) texto = new string(' ', iniciais) + texto;
                    }
                    atual = texto;
                }
                else
                {
                    atual = atual + " " + texto;
                }

                var proxima = i + 1 < linhasVisuais.Count ? linhasVisuais[i + 1] : null;
                bool quebraVisual = proxima != null && proxima.Palavras.Count > 0 &&
                    visual.XFim + larguraEspaco + proxima.Palavras[0].Largura > margemDireita - 0.5;

                if (!quebraVisual)
                {
                    linhasSap.Add(atual);
                    atual = null;
                }
            }
            if (atual != null) linhasSap.Add(atual);
            return linhasSap;
        }

        /// <summary>Une linhas SAP: sem espaço quando a anterior estava cheia (132 = corte no meio da palavra), senão com espaço.</summary>
        public static string JuntarLinhasSap(IEnumerable<string> linhasSap, int largura = LarguraLinhaSap)
        {
            var resultado = new StringBuilder();
            string anterior = null;
            foreach (var linha in linhasSap)
            {
                if (string.IsNullOrEmpty(linha)) continue;
                // linha anterior incompleta = fim de parágrafo → separa com espaço (se a linha já não começar com um)
                if (anterior != null && anterior.Length < largura && !linha.StartsWith(' ')) resultado.Append(' ');
                resultado.Append(linha);
                anterior = linha;
            }
            return resultado.ToString().Trim();
        }

        /// <summary>'#' é como o SAP mostra quebras de linha/tabulação no texto. Troca por espaço e normaliza.</summary>
        public static string LimparTexto(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return texto;
            var semMarcas = texto.Replace('#', ' ');
            return Regex.Replace(semMarcas, @"\s{2,}", " ").Trim();
        }

        // ------------------------------------------------------------------ datas

        private static DateTime? ConverterData(string valor) =>
            DateTime.TryParseExact(valor?.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;

        private static DateTime? ConverterDataHora(string valor)
        {
            if (string.IsNullOrWhiteSpace(valor)) return null;
            var formatos = new[] { "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm", "dd.MM.yyyy" };
            return DateTime.TryParseExact(valor.Trim(), formatos, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : null;
        }
    }
}
