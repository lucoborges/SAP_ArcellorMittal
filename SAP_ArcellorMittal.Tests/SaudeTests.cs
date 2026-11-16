using System.Net;
using System.Text.Json;
using SAP_ArcellorMittal.Services;

namespace SAP_ArcellorMittal.Tests;

/// <summary>Rota de saúde (GET /health) e a regra que decide se o robô está saudável.</summary>
public class SaudeTests
{
    private static readonly DateTime Agora = new DateTime(2026, 11, 13, 12, 0, 0);

    private static EstadoSnapshot Estado(DateTime inicio, DateTime? fimUltimo = null, int falhas = 0, int intervalo = 60) =>
        new EstadoSnapshot { Inicio = inicio, IntervaloMinutos = intervalo, FimUltimoCiclo = fimUltimo, FalhasGravesSeguidas = falhas };

    [Fact]
    public void Recem_iniciado_e_saudavel_mesmo_sem_ciclo_concluido()
    {
        var (ok, motivo) = EstadoRobo.Avaliar(Estado(Agora.AddMinutes(-5)), Agora);
        Assert.True(ok);
        Assert.Equal("iniciando", motivo);
    }

    [Fact]
    public void Ciclo_recente_e_saudavel()
    {
        var (ok, motivo) = EstadoRobo.Avaliar(Estado(Agora.AddDays(-3), Agora.AddMinutes(-70)), Agora);
        Assert.True(ok);
        Assert.Equal("ok", motivo);
    }

    [Fact]
    public void Sem_ciclo_concluido_ha_mais_de_dois_intervalos_e_meia_hora_nao_e_saudavel()
    {
        Assert.False(EstadoRobo.Avaliar(Estado(Agora.AddDays(-3), Agora.AddMinutes(-151)), Agora).Saudavel);
        Assert.True(EstadoRobo.Avaliar(Estado(Agora.AddDays(-3), Agora.AddMinutes(-149)), Agora).Saudavel);
        // nunca concluiu nenhum ciclo desde a partida
        Assert.False(EstadoRobo.Avaliar(Estado(Agora.AddMinutes(-151)), Agora).Saudavel);
    }

    [Fact]
    public void Tres_falhas_graves_seguidas_nao_e_saudavel()
    {
        Assert.True(EstadoRobo.Avaliar(Estado(Agora.AddDays(-1), Agora.AddMinutes(-10), falhas: 2), Agora).Saudavel);
        Assert.False(EstadoRobo.Avaliar(Estado(Agora.AddDays(-1), Agora.AddMinutes(-10), falhas: 3), Agora).Saudavel);
    }

    [Fact]
    public void Falha_parcial_ou_sucesso_zera_a_contagem_de_falhas_graves()
    {
        EstadoRobo.Reiniciar(60, 2);
        EstadoRobo.ConcluirCiclo(1, 0, null);
        EstadoRobo.ConcluirCiclo(1, 0, null);
        Assert.Equal(2, EstadoRobo.Snapshot().FalhasGravesSeguidas);
        EstadoRobo.ConcluirCiclo(2, 1, null);
        Assert.Equal(0, EstadoRobo.Snapshot().FalhasGravesSeguidas);
    }

    [Fact]
    public async Task Rota_health_responde_200_com_json_sem_dados_sensiveis()
    {
        EstadoRobo.Reiniciar(60, 2);
        EstadoRobo.IniciarCiclo(1);
        EstadoRobo.ConcluirCiclo(0, 3, DateTime.Now.AddMinutes(60));
        using var servidor = ServidorSaude.Iniciar("127.0.0.1", 0, null);
        using var http = new HttpClient();

        var resposta = await http.GetAsync($"http://127.0.0.1:{servidor.Porta}/health");
        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        var texto = await resposta.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(texto).RootElement;
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal(2, json.GetProperty("contas").GetInt32());
        Assert.Equal(3, json.GetProperty("novasUltimoCiclo").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("versao").GetString()));
        Assert.DoesNotContain("senha", texto, StringComparison.OrdinalIgnoreCase);

        // /saude é a mesma rota; qualquer outra não existe; só GET
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{servidor.Porta}/saude")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{servidor.Porta}/outra")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync($"http://127.0.0.1:{servidor.Porta}/health", null)).StatusCode);
    }

    [Fact]
    public async Task Rota_health_responde_503_quando_o_robo_nao_esta_saudavel()
    {
        EstadoRobo.Reiniciar(60, 1, inicio: DateTime.Now.AddHours(-5)); // 5 h no ar sem concluir ciclo
        using var servidor = ServidorSaude.Iniciar("127.0.0.1", 0, null);
        using var http = new HttpClient();

        var resposta = await http.GetAsync($"http://127.0.0.1:{servidor.Porta}/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Contains("\"status\":\"falha\"", await resposta.Content.ReadAsStringAsync());
        EstadoRobo.Reiniciar(60, 1);
    }

    [Fact]
    public async Task Com_token_a_rota_exige_autenticacao()
    {
        EstadoRobo.Reiniciar(60, 1);
        using var servidor = ServidorSaude.Iniciar("127.0.0.1", 0, "token-de-teste");
        var url = $"http://127.0.0.1:{servidor.Porta}/health";
        using var http = new HttpClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(url)).StatusCode);

        var errado = new HttpRequestMessage(HttpMethod.Get, url);
        errado.Headers.Add("X-Api-Key", "token-errado");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(errado)).StatusCode);

        var chave = new HttpRequestMessage(HttpMethod.Get, url);
        chave.Headers.Add("X-Api-Key", "token-de-teste");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(chave)).StatusCode);

        var bearer = new HttpRequestMessage(HttpMethod.Get, url);
        bearer.Headers.Add("Authorization", "Bearer token-de-teste");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(bearer)).StatusCode);
    }

    [Fact]
    public void Sem_token_a_rota_so_escuta_no_proprio_servidor()
    {
        Assert.Throws<InvalidOperationException>(() => ServidorSaude.Iniciar("0.0.0.0", 0, null));
        Assert.Throws<InvalidOperationException>(() => ServidorSaude.Iniciar("*", 0, ""));
    }
}
