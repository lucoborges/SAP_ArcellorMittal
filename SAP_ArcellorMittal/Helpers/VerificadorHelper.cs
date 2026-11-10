using System;
using System.Threading.Tasks;
using Microsoft.Playwright;
using SAP_ArcellorMittal.Config;

namespace SAP_ArcellorMittal.Helpers
{
    public static class VerificadorHelper
    {
        public static async Task<bool> VerificarBotaoAvanc(IPage page)
        {
            Log.Detalhe("\n=== VERIFICANDO BOTÃO AVANÇ. ===");

            try
            {
                await Task.Delay(3000);
                
                foreach (var frame in page.Frames)
                {
                    if (frame == null) continue;
                    
                    try
                    {
                        var botaoAvanc = await frame.QuerySelectorAsync(Settings.SeletorAvancCaption);
                        
                        if (botaoAvanc != null)
                        {
                            var texto = await botaoAvanc.TextContentAsync();
                            if (texto != null && texto.Trim() == Settings.TextoAvanc)
                            {
                                Log.Detalhe("✅ Botão 'Avanç.' encontrado!");
                                
                                var botaoPai = await frame.QuerySelectorAsync(Settings.SeletorAvanc);
                                if (botaoPai != null)
                                {
                                    Log.Detalhe("🔍 Clicando no botão 'Avanç.'...");
                                    await botaoPai.ClickAsync();
                                    Log.Detalhe("✅ Botão 'Avanç.' clicado!");
                                    
                                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                                    await Task.Delay(Settings.DelayLongo);
                                }
                                else
                                {
                                    Log.Detalhe("🔍 Tentando clicar no caption do botão...");
                                    await botaoAvanc.ClickAsync();
                                    Log.Detalhe("✅ Botão 'Avanç.' clicado via caption!");
                                    
                                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                                    await Task.Delay(Settings.DelayLongo);
                                }
                                
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Detalhe($"Erro ao verificar botão no frame: {ex.Message}");
                    }
                }
                
                Log.Detalhe("ℹ️ Botão 'Avanç.' não encontrado. Continuando normalmente...");
                return false;
            }
            catch (Exception ex)
            {
                Log.Aviso($"⚠️ Erro ao verificar botão 'Avanç.': {ex.Message}");
                return false;
            }
        }
    }
}
