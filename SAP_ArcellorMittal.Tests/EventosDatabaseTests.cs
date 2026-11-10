using SAP_ArcellorMittal.Models;
using SAP_ArcellorMittal.Services;

namespace SAP_ArcellorMittal.Tests;

public class EventosDatabaseTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "arcelor_db_" + Guid.NewGuid().ToString("N"));
    private string Arquivo => Path.Combine(_pasta, "eventos.json");

    public EventosDatabaseTests() => Directory.CreateDirectory(_pasta);
    public void Dispose() { try { Directory.Delete(_pasta, true); } catch { } }

    [Fact]
    public void Registro_sem_conta_vale_para_todas_as_contas()
    {
        var banco = new EventosDatabase(Arquivo);
        banco.Carregar();
        Assert.True(File.Exists(Arquivo));

        banco.Salvar(new EventoProcessado { Conta = "", NumeroEvento = "7000000001" });
        Assert.True(banco.Contem("F000000001", "7000000001"));
        Assert.True(banco.Contem("S0000000002", "7000000001"));
        Assert.Empty(banco.EmailsPendentes());
    }

    [Fact]
    public void Evento_de_uma_conta_nao_bloqueia_a_outra_conta()
    {
        var banco = new EventosDatabase(Arquivo);
        banco.Carregar();
        banco.Salvar(new EventoProcessado { Conta = "S0000000002", NumeroEvento = "7000000571", EmailPendente = true });

        var recarregado = new EventosDatabase(Arquivo);
        recarregado.Carregar();
        Assert.True(recarregado.Contem("S0000000002", "7000000571"));
        Assert.False(recarregado.Contem("F000000001", "7000000571"));
        Assert.Single(recarregado.EmailsPendentes());
    }

    [Fact]
    public void Salvar_atualiza_o_registro_e_preserva_o_que_outra_execucao_gravou()
    {
        var a = new EventosDatabase(Arquivo);
        a.Carregar();
        var b = new EventosDatabase(Arquivo);
        b.Carregar();

        var evento = new EventoProcessado { Conta = "F000000001", NumeroEvento = "7000000010", EmailPendente = true };
        a.Salvar(evento);
        b.Salvar(new EventoProcessado { Conta = "F000000001", NumeroEvento = "7000000020" });
        evento.EmailPendente = false;
        a.Salvar(evento);

        var final = new EventosDatabase(Arquivo);
        final.Carregar();
        Assert.Equal(2, final.Eventos.Count);
        Assert.Empty(final.EmailsPendentes());
    }

    [Fact]
    public void Corpo_do_email_traz_conta_numero_prazo_e_itens()
    {
        var e = new EventoProcessado
        {
            Conta = "S0000000002",
            NumeroEvento = "7000000343",
            DadosPortal = new() { ["Descrição de eventos"] = "SENSOR DE NIVEL TESTE", ["Tipo de evento"] = "Sol. Cot. Brasil" },
            PrazoApresentacao = new DateTime(2026, 11, 12, 17, 0, 0),
            Comprador = "Fulano",
            Itens = { new ItemResumo { NumeroItem = "10", Quantidade = "2", Unidade = "UN", Denominacao = "SENSOR", NumeroProduto = "123" } }
        };

        Assert.Equal("Nova cotação ArcelorMittal 7000000343 - SENSOR DE NIVEL TESTE", EmailService.MontarAssunto(e));
        var corpo = EmailService.MontarCorpo(e);
        Assert.Contains("Conta do portal: S0000000002", corpo);
        Assert.Contains("Prazo de apresentação: 12/11/2026 17:00", corpo);
        Assert.Contains("Item 10: 2 UN — SENSOR (123)", corpo);
        Assert.Contains("Tipo de evento: Sol. Cot. Brasil", corpo);
    }
}
