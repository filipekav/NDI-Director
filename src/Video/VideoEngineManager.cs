using System;
using System.Threading;

// ===========================================================================
// GERENCIADOR CENTRALIZADO DE MOTORES DE VÍDEO (CPU / GPU)
// ===========================================================================
public static class VideoEngineManager
{
    private static readonly object _lockTroca = new();

    /// <summary>
    /// Inicia o motor de vídeo configurado inicialmente no bootstrap.
    /// </summary>
    public static void IniciarMotorConfigurado()
    {
        lock (_lockTroca)
        {
            if (AppConfig.MotorVideo == "gpu")
            {
                Console.WriteLine("[*] Motor de vídeo selecionado: GPU (DirectX 11 + Direct2D)");
                VideoEngineGpu.Iniciar();
            }
            else
            {
                Console.WriteLine("[*] Motor de vídeo selecionado: CPU (OpenCV + GDI+)");
                VideoEngine.Iniciar();
            }
        }
    }

    /// <summary>
    /// Reinicia ou troca o motor de vídeo em tempo de execução (hot-swap) sem desligar a aplicação.
    /// </summary>
    /// <param name="novoMotor">"gpu", "cpu" ou null (para apenas reiniciar o atual)</param>
    public static void ReiniciarMotor(string? novoMotor = null)
    {
        lock (_lockTroca)
        {
            if (!string.IsNullOrEmpty(novoMotor) && (novoMotor == "gpu" || novoMotor == "cpu"))
            {
                AppConfig.MotorVideo = novoMotor;
                AppConfig.SalvarConfiguracoes();
            }

            string motorAlvo = AppConfig.MotorVideo;
            Console.WriteLine($"[*] Reiniciando Motor de Vídeo para: {motorAlvo.ToUpper()}...");

            // 1. Para ambos os motores com segurança
            try { VideoEngineGpu.Parar(); } catch (Exception ex) { Console.WriteLine($"[!] Erro ao parar VideoEngineGpu: {ex.Message}"); }
            try { VideoEngine.Parar(); } catch (Exception ex) { Console.WriteLine($"[!] Erro ao parar VideoEngine: {ex.Message}"); }

            Thread.Sleep(200);

            // 2. Inicia o motor desejado
            if (motorAlvo == "gpu")
            {
                Console.WriteLine("[*] Motor GPU (DirectX 11) iniciado com sucesso.");
                VideoEngineGpu.Iniciar();
            }
            else
            {
                Console.WriteLine("[*] Motor CPU (OpenCV + GDI+) iniciado com sucesso.");
                VideoEngine.Iniciar();
            }

            // 3. Notifica clientes web via SSE
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Motor de vídeo reiniciado com sucesso para '{(motorAlvo == "gpu" ? "GPU (DirectX 11)" : "CPU (OpenCV)")}'.", "sucesso");
        }
    }
}
