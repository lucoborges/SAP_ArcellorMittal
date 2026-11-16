using SAP_ArcellorMittal.Config;
using SAP_ArcellorMittal.Helpers;

// O log e o estado do robô são estáticos (um por processo): os testes rodam em sequência
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SAP_ArcellorMittal.Tests;

public class LogEConfigTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "arcelor_log_" + Guid.NewGuid().ToString("N"));

    public LogEConfigTests() => Directory.CreateDirectory(_pasta);

    public void Dispose()
    {
        Log.Configurar(Path.Combine(Path.GetTempPath(), "arcelor_log_descartavel"), 30, 10);
        try { Directory.Delete(_pasta, true); } catch { }
    }

    [Fact]
    public void Log_grava_na_pasta_configurada_e_nunca_grava_segredos()
    {
        Log.Configurar(_pasta, 30, 10);
        Log.RegistrarSegredo("S3nh@-Muito-Secreta");

        Log.Detalhe("Falha de autenticação: 535 senha S3nh@-Muito-Secreta recusada");

        var texto = File.ReadAllText(Directory.GetFiles(_pasta, "robo_*.log").Single());
        Assert.Contains("Falha de autenticação", texto);
        Assert.Contains("***", texto);
        Assert.DoesNotContain("S3nh@-Muito-Secreta", texto);
    }

    [Fact]
    public void Log_rotaciona_ao_passar_do_tamanho_maximo()
    {
        Log.Configurar(_pasta, 30, 10);
        Log.DefinirTamanhoMaxBytes(300);

        for (int i = 0; i < 20; i++) Log.Detalhe($"linha {i:000} " + new string('x', 40));

        var arquivos = Directory.GetFiles(_pasta, "robo_*.log");
        Assert.True(arquivos.Length > 1, "deveria ter criado arquivos numerados (robo_AAAAMMDD.N.log)");
        Assert.Contains(arquivos, a => Path.GetFileName(a).Count(c => c == '.') == 2);
        Assert.Equal(20, arquivos.Sum(a => File.ReadAllLines(a).Length)); // nenhuma linha perdida
    }

    [Fact]
    public void Log_apaga_arquivos_mais_antigos_que_o_limite()
    {
        Log.Configurar(_pasta, 7, 10);
        var velho = Path.Combine(_pasta, "robo_20200101.log");
        var novo = Path.Combine(_pasta, "robo_20991231.log");
        File.WriteAllText(velho, "x");
        File.WriteAllText(novo, "x");
        File.SetLastWriteTime(velho, DateTime.Now.AddDays(-8));

        Log.LimparAntigos();

        Assert.False(File.Exists(velho));
        Assert.True(File.Exists(novo));
    }

    [Fact]
    public void Config_le_enderecos_portas_e_logs_do_arquivo()
    {
        var arquivo = Path.Combine(_pasta, "config.local.json");
        File.WriteAllText(arquivo, """
            {
              // comentários são aceitos
              "Contas": [ "USUARIO_1" ],
              "UrlPortal": "https://portal.exemplo/",
              "NavegadorExecutavel": "C:\\Chrome\\chrome.exe",
              "IntervaloMinutos": 30,
              "PastaLogs": "D:\\logs",
              "SaudeEndereco": "127.0.0.1",
              "SaudePorta": 5999,
            }
            """);

        var config = ConfigLocal.Carregar(arquivo);

        Assert.Equal("USUARIO_1", Assert.Single(config.Contas));
        Assert.Equal("https://portal.exemplo/", config.UrlPortal);
        Assert.Equal(@"C:\Chrome\chrome.exe", config.NavegadorExecutavel);
        Assert.Equal(30, config.IntervaloMinutos);
        Assert.Equal(@"D:\logs", config.PastaLogs);
        Assert.Equal(5999, config.SaudePorta);
        Assert.Equal(587, config.SmtpPorta); // padrão preservado
    }

    [Fact]
    public void Config_ausente_devolve_padroes_e_config_invalida_explica_o_arquivo()
    {
        Assert.Empty(ConfigLocal.Carregar(Path.Combine(_pasta, "nao-existe.json")).Contas);

        var quebrado = Path.Combine(_pasta, "quebrado.json");
        File.WriteAllText(quebrado, "{ isto não é json");
        var erro = Assert.Throws<InvalidOperationException>(() => ConfigLocal.Carregar(quebrado));
        Assert.Contains("quebrado.json", erro.Message);
    }
}
