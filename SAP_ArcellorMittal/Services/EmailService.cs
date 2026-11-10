using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;
using SAP_ArcellorMittal.Models;

namespace SAP_ArcellorMittal.Services
{
    /// <summary>
    /// Envia um e-mail por cotação processada (remetente → destinatário da configuração), com o PDF da cotação anexado.
    /// SMTP Locaweb 587/STARTTLS — mesma configuração do ComprasGOV.
    /// </summary>
    public class EmailService
    {
        private readonly bool _desativado;

        /// <param name="desativado">--sem-email: não envia; os e-mails ficam pendentes no banco para a próxima execução.</param>
        public EmailService(bool desativado = false) => _desativado = desativado;

        public bool Configurado => !string.IsNullOrWhiteSpace(Settings.SmtpSenha);

        public static string MontarAssunto(EventoProcessado e)
        {
            var descricao = Valor(e.DadosPortal, "Descrição de eventos") ?? e.Descricao;
            var assunto = $"Nova cotação ArcelorMittal {e.NumeroEvento}";
            return string.IsNullOrWhiteSpace(descricao) ? assunto : $"{assunto} - {descricao.Trim()}";
        }

        public static string MontarCorpo(EventoProcessado e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Nova solicitação de cotação no portal ArcelorMittal (SRM).");
            sb.AppendLine();
            sb.AppendLine($"Conta do portal: {e.Conta}");
            sb.AppendLine($"Nº evento: {e.NumeroEvento}{(string.IsNullOrWhiteSpace(e.Versao) ? "" : $" (versão {e.Versao})")}");
            Linha(sb, "Descrição", e.Descricao ?? Valor(e.DadosPortal, "Descrição de eventos"));
            Linha(sb, "Tipo de evento", Valor(e.DadosPortal, "Tipo de evento"));
            Linha(sb, "Status do evento", Valor(e.DadosPortal, "Status do evento"));
            if (e.PrazoApresentacao.HasValue)
                sb.AppendLine($"Prazo de apresentação: {e.PrazoApresentacao:dd/MM/yyyy HH:mm}");
            else
                Linha(sb, "Data final", Valor(e.DadosPortal, "Data final"));
            if (e.DataAbertura.HasValue) sb.AppendLine($"Data de abertura: {e.DataAbertura:dd/MM/yyyy}");
            Linha(sb, "Comprador", e.Comprador);
            Linha(sb, "E-mail do comprador", e.EmailComprador);
            Linha(sb, "Telefone do comprador", e.TelefoneComprador);

            sb.AppendLine();
            sb.AppendLine($"Itens ({e.Itens.Count}):");
            foreach (var i in e.Itens)
            {
                var produto = string.IsNullOrWhiteSpace(i.NumeroProduto) ? "" : $" ({i.NumeroProduto})";
                var remessa = i.DataRemessa.HasValue ? $" — remessa {i.DataRemessa:dd/MM/yyyy}" : "";
                sb.AppendLine($"  • Item {i.NumeroItem}: {i.Quantidade} {i.Unidade} — {i.Denominacao}{produto}{remessa}");
                if (!string.IsNullOrWhiteSpace(i.TextoLongo))
                    sb.AppendLine($"      {i.TextoLongo}");
            }

            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(e.ArquivoPdf))
                sb.AppendLine($"PDF da cotação: {e.ArquivoPdf}");
            sb.AppendLine();
            sb.AppendLine("Mensagem automática do robô SAP ArcelorMittal.");
            return sb.ToString();
        }

        /// <summary>Envia o e-mail do evento. Lança exceção em caso de falha.</summary>
        public async Task EnviarAsync(EventoProcessado e)
        {
            var senha = Settings.SmtpSenha;
            if (string.IsNullOrWhiteSpace(senha))
                throw new InvalidOperationException("senha SMTP ausente: defina a variável de ambiente ARCELOR_SMTP_SENHA (ou COMPRASGOV_SMTP_SENHA)");

            using var mensagem = new MailMessage
            {
                From = new MailAddress(Settings.EmailRemetente),
                Subject = MontarAssunto(e),
                SubjectEncoding = Encoding.UTF8,
                Body = MontarCorpo(e),
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false,
            };
            mensagem.To.Add(Settings.EmailDestino);

            if (!string.IsNullOrWhiteSpace(e.ArquivoPdf) && File.Exists(e.ArquivoPdf))
                mensagem.Attachments.Add(new Attachment(e.ArquivoPdf));

            using var smtp = new SmtpClient(Settings.SmtpHost, Settings.SmtpPorta)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(Settings.EmailRemetente, senha),
                Timeout = 180_000,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            await smtp.SendMailAsync(mensagem);
        }

        /// <summary>
        /// Tenta enviar e registra o resultado no próprio evento (EmailPendente / EmailEnviadoEm / EmailErro).
        /// Não lança exceção: se falhar, o evento continua pendente e é reenviado na próxima execução.
        /// </summary>
        public async Task<bool> TentarEnviarAsync(EventoProcessado e)
        {
            if (_desativado)
            {
                e.EmailPendente = true;
                Log.Detalhe($"   ✉️ --sem-email: e-mail da cotação {e.NumeroEvento} fica pendente.");
                return false;
            }

            try
            {
                await EnviarAsync(e);
                e.EmailPendente = false;
                e.EmailEnviadoEm = DateTime.Now;
                e.EmailErro = null;
                Log.Info($"   📧 E-mail da cotação {e.NumeroEvento} enviado para {Settings.EmailDestino}.");
                return true;
            }
            catch (Exception ex)
            {
                e.EmailPendente = true;
                e.EmailErro = $"{DateTime.Now:dd/MM/yyyy HH:mm}: {ex.Message}";
                Log.Aviso($"   ⚠️ E-mail da cotação {e.NumeroEvento} NÃO enviado ({ex.Message}). Será tentado de novo na próxima execução.");
                return false;
            }
        }

        private static void Linha(StringBuilder sb, string rotulo, string valor)
        {
            if (!string.IsNullOrWhiteSpace(valor)) sb.AppendLine($"{rotulo}: {valor.Trim()}");
        }

        private static string Valor(Dictionary<string, string> dados, string chave) =>
            dados != null && dados.TryGetValue(chave, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}
