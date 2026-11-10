using System;
using System.Collections.Generic;

namespace SAP_ArcellorMittal.Models
{
    /// <summary>
    /// Um evento (solicitação de cotação) já processado pelo robô, gravado no banco JSON
    /// (arquivo arcelormittal_eventos.json, na pasta "PastaBanco" da configuração).
    /// </summary>
    public class EventoProcessado
    {
        /// <summary>Usuário do portal em que o evento foi encontrado. Vazio = registro antigo (vale para todas as contas).</summary>
        public string Conta { get; set; } = "";
        public string NumeroEvento { get; set; }

        /// <summary>
        /// Colunas da linha do evento na tabela do portal, com os mesmos nomes da exportação
        /// "todos_os_eventos" (Descrição de eventos, Tipo de evento, Status do evento, Data final, Nº cotação...).
        /// </summary>
        public Dictionary<string, string> DadosPortal { get; set; } = new Dictionary<string, string>();

        // Dados lidos do PDF de "Visualização"
        public string Descricao { get; set; }
        public string Versao { get; set; }
        public string Comprador { get; set; }
        public string EmailComprador { get; set; }
        public string TelefoneComprador { get; set; }
        public DateTime? PrazoApresentacao { get; set; }
        public DateTime? DataAbertura { get; set; }
        public List<ItemResumo> Itens { get; set; } = new List<ItemResumo>();

        public string ArquivoPdf { get; set; }
        public DateTime ProcessadoEm { get; set; }

        /// <summary>De onde veio o registro: "portal" ou "migrado (numeros_eventos.txt)".</summary>
        public string Origem { get; set; } = "portal";

        /// <summary>true enquanto o e-mail da cotação ainda não foi enviado (é tentado de novo nas próximas execuções).</summary>
        public bool EmailPendente { get; set; }
        public DateTime? EmailEnviadoEm { get; set; }
        public string EmailErro { get; set; }
    }

    public class ItemResumo
    {
        public string NumeroItem { get; set; }
        public string Quantidade { get; set; }
        public string Unidade { get; set; }
        public string NumeroProduto { get; set; }
        public string Denominacao { get; set; }
        public string TextoLongo { get; set; }
        public DateTime? DataRemessa { get; set; }
    }
}
