using System.Runtime.InteropServices;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// ESCANEADOR DE FONTES NDI (DISCOVERY)
// ===========================================================================
public static class NdiScanner
{
    private static Thread? _scanThread;
    private static bool _running = false;

    public static void Iniciar()
    {
        _running = true;
        _scanThread = new Thread(ScanLoop)
        {
            IsBackground = true,
            Name = "NDI_Scanner"
        };
        _scanThread.Start();
    }

    public static void Parar()
    {
        _running = false;
        _scanThread?.Join(1000);
    }

    private static void ScanLoop()
    {
        var findSettings = new NDIlib.find_create_t { show_local_sources = true };
        IntPtr pFind = NDIlib.find_create_v2(ref findSettings);
        if (pFind == IntPtr.Zero) return;

        while (_running)
        {
            NDIlib.find_wait_for_sources(pFind, 100);

            uint numSources = 0;
            IntPtr sourcesPtr = NDIlib.find_get_current_sources(pFind, ref numSources);

            var fontesNaRede = new List<string>();
            int structSize = Marshal.SizeOf(typeof(NDIlib.source_t));
            
            for (int i = 0; i < numSources; i++)
            {
                IntPtr elementPtr = IntPtr.Add(sourcesPtr, i * structSize);
                var source = Marshal.PtrToStructure<NDIlib.source_t>(elementPtr);
                string? name = Marshal.PtrToStringAnsi(source.p_ndi_name);
                
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.Contains("MESA_NDI_MOSAICO") || name.Contains("MESA_NDI_VERTICAL") || name.Contains("MESA_NDI_AUDIO") || name.Contains("Orador ativo") || name.Contains("Orador Ativo") || name.Contains("MS Teams - (Local)"))
                        continue;
                        
                    fontesNaRede.Add(name);
                }
            }

            lock (AppConfig.LockFontes)
            {
                bool mudou = fontesNaRede.Count != AppConfig.FontesNaRede.Count;
                if (!mudou)
                {
                    foreach (var f in fontesNaRede)
                    {
                        if (!AppConfig.FontesNaRede.Contains(f))
                        {
                            mudou = true;
                            break;
                        }
                    }
                }

                if (mudou)
                {
                    AppConfig.FontesNaRede = fontesNaRede;

                    // Remoção automática das câmeras dos participantes que saíram da reunião (rede)
                    var nomesAtivos = AppConfig.ReceptoresAtivos.Keys.ToList();
                    foreach (var nomeAtivo in nomesAtivos)
                    {
                        if (!fontesNaRede.Contains(nomeAtivo))
                        {
                            if (AppConfig.ReceptoresAtivos.TryRemove(nomeAtivo, out var rec))
                            {
                                Task.Run(() => rec.Parar());
                            }

                            // Para a gravação associada a este feed, se estiver ativa
                            if (AppConfig.GravadoresAtivos.TryRemove(nomeAtivo, out var g))
                            {
                                Task.Run(() => g.Parar());
                            }

                            for (int i = 0; i < 4; i++)
                            {
                                if (AppConfig.OrdemReceptores[i] == nomeAtivo)
                                {
                                    AppConfig.OrdemReceptores[i] = null;
                                }
                            }

                            if (AppConfig.FonteHighlight == nomeAtivo) AppConfig.FonteHighlight = null;
                            if (AppConfig.FonteSolo == nomeAtivo) AppConfig.FonteSolo = null;

                            Console.WriteLine($"[Auto-Remove] Participante '{nomeAtivo}' saiu da reuniao. Camera removida do canvas.");
                            SseManager.LogAtividade($"Participante '{nomeAtivo}' desconectou da rede local.", "aviso");
                        }
                    }

                    SseManager.NotificarClientes();
                }

                // Sincronizar Receptores de Preview em segundo plano (Low Bandwidth)
                if (AppConfig.HabilitarLivePreview)
                {
                    var previewsParaRemover = AppConfig.ReceptoresPreview.Keys
                        .Where(nome => AppConfig.ReceptoresAtivos.ContainsKey(nome) || !fontesNaRede.Contains(nome))
                        .ToList();

                    foreach (var nome in previewsParaRemover)
                    {
                        if (AppConfig.ReceptoresPreview.TryRemove(nome, out var rec))
                        {
                            Task.Run(() => rec.Parar());
                        }
                    }

                    foreach (var nome in fontesNaRede)
                    {
                        if (!AppConfig.ReceptoresAtivos.ContainsKey(nome) && !AppConfig.ReceptoresPreview.ContainsKey(nome))
                        {
                            try
                            {
                                var recPreview = new ReceptorNDI(nome, lowBandwidth: true);
                                AppConfig.ReceptoresPreview[nome] = recPreview;
                                SseManager.LogAtividade($"Nova fonte NDI detectada na rede local: '{nome}'", "normal");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] Erro ao criar receptor de preview para '{nome}': {ex.Message}");
                            }
                        }
                    }
                }
                else if (AppConfig.ReceptoresPreview.Count > 0)
                {
                    foreach (var nome in AppConfig.ReceptoresPreview.Keys.ToList())
                    {
                        if (AppConfig.ReceptoresPreview.TryRemove(nome, out var rec))
                        {
                            Task.Run(() => rec.Parar());
                        }
                    }
                    AppConfig.ReceptoresPreview.Clear();
                }
            }

            Thread.Sleep(2000);
        }

        NDIlib.find_destroy(pFind);
    }
}
