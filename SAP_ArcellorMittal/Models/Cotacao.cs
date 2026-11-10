using System;
using System.Collections.Generic;

namespace SAP_ArcellorMittal.Models
{
    /// <summary>
    /// Dados de uma solicitação de cotação extraídos do PDF de "Visualização" do SRM.
    /// </summary>
    public class Cotacao
    {
        public string NumeroSolicitacao { get; set; }
        public string Descricao { get; set; }
        public string Comprador { get; set; }
        public string Telefone { get; set; }
        public string Email { get; set; }
        public string Versao { get; set; }
        public DateTime? PrazoApresentacao { get; set; }
        public DateTime? DataAbertura { get; set; }
        public List<ItemCotacao> Itens { get; } = new List<ItemCotacao>();

        /// <summary>Caminho do PDF de onde os dados foram lidos.</summary>
        public string ArquivoPdf { get; set; }

        /// <summary>Avisos de leitura (campos não encontrados, linhas não reconhecidas etc.).</summary>
        public List<string> Avisos { get; } = new List<string>();
    }

    public class ItemCotacao
    {
        public string NumeroItem { get; set; }
        public string Categoria { get; set; }
        public string Aplicacao { get; set; }
        public DateTime? DataRemessa { get; set; }
        public decimal? Quantidade { get; set; }
        public string QuantidadeTexto { get; set; }
        public string Unidade { get; set; }
        public string NumeroProduto { get; set; }
        public string Denominacao { get; set; }
        public string Centro { get; set; }

        /// <summary>Texto longo ("Texto de pedido de compra") exatamente como veio do SAP, com os '#'.</summary>
        public string TextoLongoOriginal { get; set; }

        /// <summary>Texto longo limpo: '#' (quebras de linha do SAP) viram espaço e espaços repetidos são removidos.</summary>
        public string TextoLongo { get; set; }
    }
}
