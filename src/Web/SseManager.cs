using System.Text.Json;
using Microsoft.AspNetCore.Http;

// ===========================================================================
// GERENCIADOR SSE (SERVER-SENT EVENTS)
// ===========================================================================
public static class SseManager
{
    private static readonly List<HttpResponse> ClientesSSE = new();
    private static readonly object LockClientes = new();
    private static bool _envioVuRodando = false;

    public static void AdicionarCliente(HttpResponse response)
    {
        lock (LockClientes)
        {
            ClientesSSE.Add(response);
        }
    }

    public static void RemoverCliente(HttpResponse response)
    {
        lock (LockClientes)
        {
            ClientesSSE.Remove(response);
        }
    }

    public static void NotificarClientes()
    {
        HttpResponse[] clientes;
        lock (LockClientes)
        {
            clientes = ClientesSSE.ToArray();
        }

        foreach (var client in clientes)
        {
            Task.Run(async () =>
            {
                try
                {
                    await client.WriteAsync("data: update\n\n");
                    await client.Body.FlushAsync();
                }
                catch
                {
                    // Falhou, o cliente desconectado será limpo
                }
            });
        }
    }

    public static void LogAtividade(string mensagem, string tipo = "normal")
    {
        HttpResponse[] clientes;
        lock (LockClientes)
        {
            clientes = ClientesSSE.ToArray();
        }

        if (clientes.Length == 0) return;

        string payloadJson = JsonSerializer.Serialize(new { msg = mensagem, tipo = tipo });
        string ssePayload = $"event: log\ndata: {payloadJson}\n\n";

        foreach (var client in clientes)
        {
            Task.Run(async () =>
            {
                try
                {
                    await client.WriteAsync(ssePayload);
                    await client.Body.FlushAsync();
                }
                catch
                {
                    // Ignora
                }
            });
        }
    }

    public static void IniciarEnvioVu()
    {
        if (_envioVuRodando) return;
        _envioVuRodando = true;

        Task.Run(async () =>
        {
            while (_envioVuRodando)
            {
                await Task.Delay(100);

                HttpResponse[] clientes;
                lock (LockClientes)
                {
                    clientes = ClientesSSE.ToArray();
                }

                if (clientes.Length == 0) continue;

                Dictionary<string, int> niveis;
                lock (AppConfig.LockVu)
                {
                    niveis = new Dictionary<string, int>(AppConfig.NiveisVu);
                }

                if (niveis.Count == 0) continue;

                try
                {
                    string payload = JsonSerializer.Serialize(niveis);
                    string message = $"event: vu\ndata: {payload}\n\n";

                    foreach (var client in clientes)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.WriteAsync(message);
                                await client.Body.FlushAsync();
                            }
                            catch
                            {
                                // Erro ao escrever, será tratado / limpo
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao enviar VU via SSE: {ex.Message}");
                }
            }
        });
    }

    public static void PararEnvioVu()
    {
        _envioVuRodando = false;
    }

    private static bool _envioMetricsRodando = false;

    public static void IniciarEnvioMetrics()
    {
        if (_envioMetricsRodando) return;
        _envioMetricsRodando = true;

        Task.Run(async () =>
        {
            var ultimoTempoCpu = DateTime.UtcNow;
            var ultimoTempoCpuProcesso = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

            while (_envioMetricsRodando)
            {
                await Task.Delay(1000);

                HttpResponse[] clientes;
                lock (LockClientes)
                {
                    clientes = ClientesSSE.ToArray();
                }

                if (clientes.Length == 0) continue;

                try
                {
                    var tempoAtual = DateTime.UtcNow;
                    var tempoCpuProcesso = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                    var tempoDecorrido = tempoAtual - ultimoTempoCpu;
                    
                    double cpuPorcentagem = 0;
                    if (tempoDecorrido.TotalMilliseconds > 100)
                    {
                        var cpuDiferenca = tempoCpuProcesso - ultimoTempoCpuProcesso;
                        cpuPorcentagem = (cpuDiferenca.TotalMilliseconds / (tempoDecorrido.TotalMilliseconds * Environment.ProcessorCount)) * 100;
                        cpuPorcentagem = Math.Round(Math.Max(0.0, Math.Min(100.0, cpuPorcentagem)), 1);
                    }

                    ultimoTempoCpu = tempoAtual;
                    ultimoTempoCpuProcesso = tempoCpuProcesso;

                    long bytesRam = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                    double ramMb = Math.Round(bytesRam / 1024.0 / 1024.0, 1);

                    // FPS do Mosaico Principal e Mosaico Vertical
                    double fpsMosaico = VideoEngine.ObterFpsMosaico();
                    double fpsVertical = VideoEngine.ObterFpsVertical();

                    // Coleta de estatísticas individuais dos receptores ativos
                    var listaFontes = new List<object>();
                    foreach (var kvp in AppConfig.ReceptoresAtivos)
                    {
                        listaFontes.Add(new
                        {
                            nome = kvp.Key,
                            fps = kvp.Value.Fps,
                            v_frames = kvp.Value.VideoFrames,
                            a_frames = kvp.Value.AudioFrames,
                            v_drop = kvp.Value.DroppedVideoFrames,
                            a_drop = kvp.Value.DroppedAudioFrames
                        });
                    }

                    var (nvencLoad, nvencSessions, gpuLoad, vramUsed, vramTotal) = NvidiaGpuMonitor.ObterMetricas();

                    var metrics = new
                    {
                        cpu = cpuPorcentagem,
                        ram = ramMb,
                        fpsMosaico = fpsMosaico,
                        fpsVertical = fpsVertical,
                        fontes = listaFontes,
                        nvencLoad = nvencLoad,
                        nvencSessions = nvencSessions,
                        nvencLimit = AppConfig.LimiteSessoesNvenc,
                        gpuLoad = gpuLoad,
                        vramUsed = vramUsed,
                        vramTotal = vramTotal
                    };

                    string payload = JsonSerializer.Serialize(metrics);
                    string message = $"event: metrics\ndata: {payload}\n\n";

                    foreach (var client in clientes)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await client.WriteAsync(message);
                                await client.Body.FlushAsync();
                            }
                            catch
                            {
                                // Limpeza tratada na escrita padrão
                            }
                        });
                    }
                }
                catch
                {
                    // Evita falhar o loop de métricas
                }
            }
        });
    }

    public static void PararEnvioMetrics()
    {
        _envioMetricsRodando = false;
        NvidiaGpuMonitor.Finalizar();
    }
}
