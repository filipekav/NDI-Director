using System.Runtime.InteropServices;
using OpenCvSharp;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// MOTOR DE VÍDEO GPU (DIRECT3D 11 + DIRECT2D HEADLESS)
// ===========================================================================
/// <summary>
/// Motor de composição de vídeo alternativo que utiliza aceleração por hardware
/// via DirectX 11 + Direct2D. Mantém a mesma interface pública do VideoEngine
/// original (CPU) para ser intercambiável.
/// </summary>
public static class VideoEngineGpu
{
    private static Thread? _engineThread;
    private static bool _running = false;
    private static readonly Dictionary<string, PosicaoFeed> _posicoesAtuais = new();
    private const float LERP_FATOR = 0.50f;

    // Métricas de FPS do Canvas Principal (Horizontal) e Mosaico Vertical
    private static int _contadorFramesMosaico = 0;
    private static int _contadorFramesVertical = 0;
    private static double _ultimoFpsMosaico = 0.0;
    private static double _ultimoFpsVertical = 0.0;
    private static DateTime _ultimoTempoFpsMosaico = DateTime.UtcNow;
    private static DateTime _ultimoTempoFpsVertical = DateTime.UtcNow;

    // Compositores GPU (um por canvas de saída)
    private static GpuCompositor? _compositorPrincipal;
    private static GpuCompositor? _compositorVertical;

    // Flag que indica se o fallback para CPU foi acionado
    private static bool _fallbackCpu = false;

    public static double ObterFpsMosaico()
    {
        var agora = DateTime.UtcNow;
        var diff = agora - _ultimoTempoFpsMosaico;
        if (diff.TotalMilliseconds >= 1000)
        {
            int frames = Interlocked.Exchange(ref _contadorFramesMosaico, 0);
            _ultimoFpsMosaico = Math.Round(frames / diff.TotalSeconds, 1);
            _ultimoTempoFpsMosaico = agora;
        }
        return _ultimoFpsMosaico;
    }

    public static double ObterFpsVertical()
    {
        var agora = DateTime.UtcNow;
        var diff = agora - _ultimoTempoFpsVertical;
        if (diff.TotalMilliseconds >= 1000)
        {
            int frames = Interlocked.Exchange(ref _contadorFramesVertical, 0);
            _ultimoFpsVertical = Math.Round(frames / diff.TotalSeconds, 1);
            _ultimoTempoFpsVertical = agora;
        }
        return _ultimoFpsVertical;
    }

    /// <summary>
    /// Indica se o motor está rodando via fallback de CPU (VideoEngine original).
    /// </summary>
    public static bool EmFallbackCpu => _fallbackCpu;

    public static void Iniciar()
    {
        _running = true;
        _fallbackCpu = false;
        _engineThread = new Thread(VideoEngineGpuLoop)
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "NDI_Video_Engine_GPU"
        };
        _engineThread.Start();
    }

    public static void Parar()
    {
        _running = false;
        _engineThread?.Join(2000);

        _compositorPrincipal?.Dispose();
        _compositorPrincipal = null;
        _compositorVertical?.Dispose();
        _compositorVertical = null;

        if (_fallbackCpu)
        {
            VideoEngine.Parar();
        }
    }

    private static unsafe void VideoEngineGpuLoop()
    {
        // Tentar inicializar os compositores GPU
        _compositorPrincipal = new GpuCompositor();
        _compositorVertical = new GpuCompositor();

        bool initOk = _compositorPrincipal.Inicializar(
            AppConfig.CanvasLarguraHorizontal, AppConfig.CanvasAlturaHorizontal);

        if (initOk)
        {
            initOk = _compositorVertical.Inicializar(
                AppConfig.CanvasLarguraVertical, AppConfig.CanvasAlturaVertical);
        }

        if (!initOk)
        {
            Console.WriteLine("[!] GPU Compositor falhou ao inicializar. Acionando fallback para motor CPU (OpenCV+GDI+)...");
            SseManager.LogAtividade("Motor GPU falhou ao inicializar. Usando fallback de CPU (OpenCV+GDI+).", "aviso");
            _compositorPrincipal?.Dispose();
            _compositorPrincipal = null;
            _compositorVertical?.Dispose();
            _compositorVertical = null;
            _fallbackCpu = true;
            VideoEngine.Iniciar();
            return;
        }

        Console.WriteLine("[*] Motor de vídeo GPU (DirectX 11 + Direct2D) inicializado com sucesso.");
        SseManager.LogAtividade("Motor de composição GPU (DirectX 11) ativo.", "normal");

        // --- Inicializar saídas NDI ---
        var sendSettings = new NDIlib.send_create_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi("MESA_NDI_MOSAICO"),
            clock_video = true,
            clock_audio = false
        };
        IntPtr pNdiSend = NDIlib.send_create(ref sendSettings);
        Marshal.FreeHGlobal(sendSettings.p_ndi_name);

        var sendSettingsV = new NDIlib.send_create_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi("MESA_NDI_VERTICAL"),
            clock_video = true,
            clock_audio = false
        };
        IntPtr pNdiSendV = NDIlib.send_create(ref sendSettingsV);
        Marshal.FreeHGlobal(sendSettingsV.p_ndi_name);

        var sendSettingsA = new NDIlib.send_create_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi("MESA_NDI_AUDIO"),
            clock_video = false,
            clock_audio = false
        };
        IntPtr pNdiSendA = NDIlib.send_create(ref sendSettingsA);
        Marshal.FreeHGlobal(sendSettingsA.p_ndi_name);

        if (pNdiSend == IntPtr.Zero || pNdiSendV == IntPtr.Zero || pNdiSendA == IntPtr.Zero)
        {
            Console.WriteLine("[!] Erro fatal: Não foi possível instanciar os outputs NDI.");
            return;
        }

        Console.WriteLine("[*] Outputs NDI 'MESA_NDI_MOSAICO', 'MESA_NDI_VERTICAL' e 'MESA_NDI_AUDIO' inicializados (GPU).");

        int currentW = AppConfig.CanvasLarguraHorizontal;
        int currentH = AppConfig.CanvasAlturaHorizontal;
        int currentWV = AppConfig.CanvasLarguraVertical;
        int currentHV = AppConfig.CanvasAlturaVertical;

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            xres = currentW,
            yres = currentH,
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            line_stride_in_bytes = currentW * 4,
            frame_rate_N = 30000,
            frame_rate_D = 1001,
            picture_aspect_ratio = (float)currentW / currentH,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive
        };

        var videoFrameV = new NDIlib.video_frame_v2_t
        {
            xres = currentWV,
            yres = currentHV,
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            line_stride_in_bytes = currentWV * 4,
            frame_rate_N = 30000,
            frame_rate_D = 1001,
            picture_aspect_ratio = (float)currentWV / currentHV,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive
        };

        // Buffer na CPU para receber o frame baixado da GPU e enviar via NDI
        IntPtr pBufferPrincipal = Marshal.AllocHGlobal(currentW * currentH * 4);
        IntPtr pBufferVertical = Marshal.AllocHGlobal(currentWV * currentHV * 4);
        IntPtr pAudioBufferNativo = Marshal.AllocHGlobal(AudioMixer.TamanhoBloco * AudioMixer.CanaisSaida * sizeof(float));

        while (_running)
        {
            var startTime = DateTime.Now;
            int pad = AppConfig.PaddingMosaico;

            // Verifica se a resolução mudou dinamicamente
            if (AppConfig.CanvasLarguraHorizontal != currentW || AppConfig.CanvasAlturaHorizontal != currentH)
            {
                currentW = AppConfig.CanvasLarguraHorizontal;
                currentH = AppConfig.CanvasAlturaHorizontal;
                _compositorPrincipal!.RedimensionarCanvas(currentW, currentH);

                videoFrame.xres = currentW;
                videoFrame.yres = currentH;
                videoFrame.line_stride_in_bytes = currentW * 4;
                videoFrame.picture_aspect_ratio = (float)currentW / currentH;

                Marshal.FreeHGlobal(pBufferPrincipal);
                pBufferPrincipal = Marshal.AllocHGlobal(currentW * currentH * 4);

                _posicoesAtuais.Clear();
            }

            if (AppConfig.CanvasLarguraVertical != currentWV || AppConfig.CanvasAlturaVertical != currentHV)
            {
                currentWV = AppConfig.CanvasLarguraVertical;
                currentHV = AppConfig.CanvasAlturaVertical;
                _compositorVertical!.RedimensionarCanvas(currentWV, currentHV);

                videoFrameV.xres = currentWV;
                videoFrameV.yres = currentHV;
                videoFrameV.line_stride_in_bytes = currentWV * 4;
                videoFrameV.picture_aspect_ratio = (float)currentWV / currentHV;

                Marshal.FreeHGlobal(pBufferVertical);
                pBufferVertical = Marshal.AllocHGlobal(currentWV * currentHV * 4);
            }

            var framesAtivos = new List<(string Nome, Mat Frame, string Apelido)>();

            lock (AppConfig.LockFontes)
            {
                for (int i = 0; i < 4; i++)
                {
                    string? nome = AppConfig.OrdemReceptores[i];
                    if (!string.IsNullOrEmpty(nome) && AppConfig.ReceptoresAtivos.TryGetValue(nome, out var rec))
                    {
                        var frame = rec.ObterFrame();
                        if (frame == null)
                        {
                            frame = new Mat(720, 1280, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                        }
                        string apelido = AppConfig.ApelidosFontes.TryGetValue(nome, out var ap) ? ap : "";
                        framesAtivos.Add((nome, frame, apelido));
                    }
                }
            }

            byte bgR, bgG, bgB, bgA;
            lock (AppConfig.LockFontes)
            {
                var col = AppConfig.CoresBackground[AppConfig.CorFundoAtual];
                bgR = col.R;
                bgG = col.G;
                bgB = col.B;
                bgA = col.A;
            }

            long timecodeComum = DateTime.UtcNow.Ticks;

            // -------------------------------------------------------------
            // Renderiza Canvas Principal (GPU)
            // -------------------------------------------------------------
            _compositorPrincipal!.IniciarFrame(bgR, bgG, bgB, bgA);

            if (framesAtivos.Count == 0)
            {
                _posicoesAtuais.Clear();
                DesenharStandbyScreen(_compositorPrincipal, currentW, currentH, false, _contadorFramesMosaico);
            }
            else
            {
                var alvos = CalcularPosicoesAlvo(framesAtivos, AppConfig.FonteHighlight, AppConfig.FonteSolo, currentW, currentH, pad);

                foreach (var kvp in alvos)
                {
                    string nome = kvp.Key;
                    var target = kvp.Value;

                    if (!_posicoesAtuais.TryGetValue(nome, out var cur))
                    {
                        cur = new PosicaoFeed(target.X, target.Y, target.W, target.H);
                        _posicoesAtuais[nome] = cur;
                    }
                    else
                    {
                        cur.X += (target.X - cur.X) * LERP_FATOR;
                        cur.Y += (target.Y - cur.Y) * LERP_FATOR;
                        cur.W += (target.W - cur.W) * LERP_FATOR;
                        cur.H += (target.H - cur.H) * LERP_FATOR;
                    }
                }

                var chavesRemover = _posicoesAtuais.Keys.Where(k => !alvos.ContainsKey(k)).ToList();
                foreach (var k in chavesRemover) _posicoesAtuais.Remove(k);

                foreach (var kvp in _posicoesAtuais)
                {
                    string nome = kvp.Key;
                    var pos = kvp.Value;
                    var feed = framesAtivos.FirstOrDefault(f => f.Nome == nome);

                    if (feed.Nome != null)
                    {
                        float x = pos.X;
                        float y = pos.Y;
                        float mw = pos.W;
                        float mh = pos.H;

                        if (mw > 0 && mh > 0)
                        {
                            bool cropV = AppConfig.MosaicoVertical && (AppConfig.FonteHighlight != nome);
                            float cropAr = cropV ? (mw / mh) : 0f;

                            _compositorPrincipal.DesenharFrame(
                                feed.Frame.Data, feed.Frame.Width, feed.Frame.Height, (int)feed.Frame.Step(),
                                x, y, mw, mh, cropV, cropAr
                            );

                            // Desenhar GC se tiver apelido
                            if (!string.IsNullOrEmpty(feed.Apelido))
                            {
                                int fontSize = 32;
                                if (mw > 1000) fontSize = 44;
                                else if (mw < 600) fontSize = 24;

                                float videoX = x;
                                float videoY = y;
                                float videoW = mw;
                                float videoH = mh;

                                if (!cropV)
                                {
                                    float escala = Math.Min(mw / feed.Frame.Width, mh / feed.Frame.Height);
                                    videoW = feed.Frame.Width * escala;
                                    videoH = feed.Frame.Height * escala;
                                    videoX = x + (mw - videoW) / 2f;
                                    videoY = y + (mh - videoH) / 2f;
                                }

                                _compositorPrincipal.DesenharGC(videoX, videoY, videoW, videoH, feed.Apelido, fontSize);
                            }
                        }
                    }
                }
            }

            _compositorPrincipal.FinalizarFrame();

            // Download da GPU para CPU e envio via NDI
            IntPtr gpuData = _compositorPrincipal.DownloadFrame(out int gpuStride);
            if (gpuData != IntPtr.Zero)
            {
                // Copia linha a linha caso o stride da GPU seja diferente
                int expectedStride = currentW * 4;
                if (gpuStride == expectedStride)
                {
                    Buffer.MemoryCopy(gpuData.ToPointer(), pBufferPrincipal.ToPointer(), currentW * currentH * 4, currentW * currentH * 4);
                }
                else
                {
                    byte* src = (byte*)gpuData.ToPointer();
                    byte* dst = (byte*)pBufferPrincipal.ToPointer();
                    for (int row = 0; row < currentH; row++)
                    {
                        Buffer.MemoryCopy(src + row * gpuStride, dst + row * expectedStride, expectedStride, expectedStride);
                    }
                }
                _compositorPrincipal.LiberarDownload();

                videoFrame.p_data = pBufferPrincipal;
                videoFrame.timecode = timecodeComum;
                NDIlib.send_send_video_v2(pNdiSend, ref videoFrame);
            }
            _contadorFramesMosaico++;

            // -------------------------------------------------------------
            // Renderiza Canvas Vertical (GPU)
            // -------------------------------------------------------------
            _compositorVertical!.IniciarFrame(bgR, bgG, bgB, bgA);

            if (framesAtivos.Count == 0)
            {
                DesenharStandbyScreen(_compositorVertical, currentWV, currentHV, true, _contadorFramesVertical);
            }
            else
            {
                int padV = 8;
                int nVis = Math.Min(framesAtivos.Count, 4);
                int hBloco = (currentHV - (nVis + 1) * padV) / nVis;

                for (int i = 0; i < nVis; i++)
                {
                    var feed = framesAtivos[i];
                    int py = padV + i * (hBloco + padV);
                    int mw = currentWV - 2 * padV;

                    _compositorVertical.DesenharFrame(
                        feed.Frame.Data, feed.Frame.Width, feed.Frame.Height, (int)feed.Frame.Step(),
                        padV, py, mw, hBloco
                    );

                    if (!string.IsNullOrEmpty(feed.Apelido))
                    {
                        int fontSize = mw < 600 ? 24 : 32;

                        float scale = Math.Min((float)mw / feed.Frame.Width, (float)hBloco / feed.Frame.Height);
                        float videoW = feed.Frame.Width * scale;
                        float videoH = feed.Frame.Height * scale;
                        float videoX = padV + (mw - videoW) / 2f;
                        float videoY = py + (hBloco - videoH) / 2f;

                        _compositorVertical.DesenharGC(videoX, videoY, videoW, videoH, feed.Apelido, fontSize);
                    }
                }
            }

            _compositorVertical.FinalizarFrame();

            IntPtr gpuDataV = _compositorVertical.DownloadFrame(out int gpuStrideV);
            if (gpuDataV != IntPtr.Zero)
            {
                int expectedStrideV = currentWV * 4;
                if (gpuStrideV == expectedStrideV)
                {
                    Buffer.MemoryCopy(gpuDataV.ToPointer(), pBufferVertical.ToPointer(), currentWV * currentHV * 4, currentWV * currentHV * 4);
                }
                else
                {
                    byte* src = (byte*)gpuDataV.ToPointer();
                    byte* dst = (byte*)pBufferVertical.ToPointer();
                    for (int row = 0; row < currentHV; row++)
                    {
                        Buffer.MemoryCopy(src + row * gpuStrideV, dst + row * expectedStrideV, expectedStrideV, expectedStrideV);
                    }
                }
                _compositorVertical.LiberarDownload();

                videoFrameV.p_data = pBufferVertical;
                videoFrameV.timecode = timecodeComum;
                NDIlib.send_send_video_v2(pNdiSendV, ref videoFrameV);
            }
            _contadorFramesVertical++;

            // -------------------------------------------------------------
            // Envia áudio mixado acumulado no mixer
            // -------------------------------------------------------------
            while (AppConfig.MixerGlobal.FilaSaida.TryDequeue(out float[]? blocoAudio))
            {
                if (AppConfig.HabilitarLogsDiagnostico)
                {
                    Console.WriteLine($"[DEBUG-AUDIO-GPU] Enviando bloco de audio mixado via NDI. Samples={AudioMixer.TamanhoBloco}");
                }
                Marshal.Copy(blocoAudio, 0, pAudioBufferNativo, blocoAudio.Length);

                var audioFrame = new NDIlib.audio_frame_v2_t
                {
                    sample_rate = AudioMixer.SampleRateSaida,
                    no_channels = AudioMixer.CanaisSaida,
                    no_samples = AudioMixer.TamanhoBloco,
                    timecode = NDIlib.send_timecode_synthesize,
                    p_data = pAudioBufferNativo,
                    channel_stride_in_bytes = AudioMixer.TamanhoBloco * sizeof(float)
                };
                NDIlib.send_send_audio_v2(pNdiSendA, ref audioFrame);
            }

            foreach (var item in framesAtivos)
            {
                item.Frame.Dispose();
            }

            double elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            int sleepTime = (int)Math.Max(1, 33.3 - elapsed);
            Thread.Sleep(sleepTime);
        }

        NDIlib.send_destroy(pNdiSend);
        NDIlib.send_destroy(pNdiSendV);
        if (pNdiSendA != IntPtr.Zero)
        {
            NDIlib.send_destroy(pNdiSendA);
        }
        Marshal.FreeHGlobal(pBufferPrincipal);
        Marshal.FreeHGlobal(pBufferVertical);
        Marshal.FreeHGlobal(pAudioBufferNativo);
    }

    // =====================================================================
    // TELA DE STANDBY (mesma lógica visual, renderizada via GPU)
    // =====================================================================
    private static void DesenharStandbyScreen(GpuCompositor compositor, int w, int h, bool isVertical, int contadorFrames)
    {
        int cx = w / 2;
        int cy = h / 2;

        // Linhas de Safe Area com cantoneiras discretas em "L"
        byte corGridV = 35;
        byte corLinhasV = 55;

        // Crosshair central
        compositor.DesenharLinha(cx - 15, cy, cx + 15, cy, corLinhasV, corLinhasV, corLinhasV);
        compositor.DesenharLinha(cx, cy - 15, cx, cy + 15, corLinhasV, corLinhasV, corLinhasV);

        // Cantoneiras de Safe Area
        void DesenharCantoneirasL(int rectW, int rectH, byte cor, int len = 15)
        {
            int rx = cx - rectW / 2;
            int ry = cy - rectH / 2;
            int rx2 = cx + rectW / 2;
            int ry2 = cy + rectH / 2;

            // Superior esquerdo
            compositor.DesenharLinha(rx, ry, rx + len, ry, cor, cor, cor);
            compositor.DesenharLinha(rx, ry, rx, ry + len, cor, cor, cor);
            // Superior direito
            compositor.DesenharLinha(rx2, ry, rx2 - len, ry, cor, cor, cor);
            compositor.DesenharLinha(rx2, ry, rx2, ry + len, cor, cor, cor);
            // Inferior esquerdo
            compositor.DesenharLinha(rx, ry2, rx + len, ry2, cor, cor, cor);
            compositor.DesenharLinha(rx, ry2, rx, ry2 - len, cor, cor, cor);
            // Inferior direito
            compositor.DesenharLinha(rx2, ry2, rx2 - len, ry2, cor, cor, cor);
            compositor.DesenharLinha(rx2, ry2, rx2, ry2 - len, cor, cor, cor);
        }

        DesenharCantoneirasL((int)(w * 0.9), (int)(h * 0.9), corGridV, isVertical ? 10 : 20);
        DesenharCantoneirasL((int)(w * 0.8), (int)(h * 0.8), corGridV, isVertical ? 8 : 15);

        // Círculo central pulsante e vetorscópio técnico
        double tempo = contadorFrames * 0.08;
        float pulse = (float)(Math.Sin(tempo) * 0.5 + 0.5);
        int raioBase = isVertical ? 100 : 130;
        int raioPulse = raioBase + (int)(pulse * 15);

        byte alphaPulse = (byte)(80 - (pulse * 50));
        compositor.DesenharCirculoBorda(cx, cy, raioPulse, 70, 70, 70, alphaPulse, 2f);
        compositor.DesenharCirculo(cx, cy, raioBase, 20, 20, 20);
        compositor.DesenharCirculoBorda(cx, cy, raioBase, 60, 60, 60, 255, 2f);

        // Ticks de 30 em 30 graus
        for (int a = 0; a < 360; a += 30)
        {
            double rad = a * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            float xStart = cx + (float)(raioBase * cos);
            float yStart = cy + (float)(raioBase * sin);
            float xEnd = cx + (float)((raioBase - 8) * cos);
            float yEnd = cy + (float)((raioBase - 8) * sin);
            compositor.DesenharLinha(xStart, yStart, xEnd, yEnd, 65, 65, 65);
        }

        // LED pulsante
        int ledY = cy - raioBase + 30;
        byte rLed = (byte)(180 + (pulse * 75));
        compositor.DesenharCirculo(cx, ledY, 6, rLed, 0, 0);

        // Barras de calibração (SMPTE Color Bars)
        int numCores = 8;
        int largBloco = isVertical ? 12 : 24;
        int totalWidth = numCores * largBloco;
        int barX = cx - totalWidth / 2;
        int barY = h - (isVertical ? 50 : 40);
        int barH = isVertical ? 8 : 14;

        // Cores superiores (BGRA -> RGB)
        (byte R, byte G, byte B)[] coresSup = new[]
        {
            ((byte)255, (byte)255, (byte)255), // Branco
            ((byte)255, (byte)255, (byte)0),   // Amarelo
            ((byte)0, (byte)255, (byte)255),   // Ciano
            ((byte)0, (byte)255, (byte)0),     // Verde
            ((byte)255, (byte)0, (byte)255),   // Magenta
            ((byte)255, (byte)0, (byte)0),     // Vermelho
            ((byte)0, (byte)0, (byte)255),     // Azul
            ((byte)30, (byte)30, (byte)30)     // Cinza Escuro
        };

        (byte R, byte G, byte B)[] coresInf = new[]
        {
            ((byte)0, (byte)0, (byte)255),     // Azul
            ((byte)15, (byte)15, (byte)15),    // Preto
            ((byte)255, (byte)0, (byte)255),   // Magenta
            ((byte)15, (byte)15, (byte)15),    // Preto
            ((byte)0, (byte)255, (byte)255),   // Ciano
            ((byte)255, (byte)255, (byte)255), // Branco
            ((byte)15, (byte)15, (byte)15),    // Preto
            ((byte)180, (byte)180, (byte)180)  // Cinza Claro
        };

        int barHSup = (int)(barH * 0.7);
        int barHInf = barH - barHSup;

        for (int i = 0; i < numCores; i++)
        {
            compositor.DesenharRetangulo(barX + i * largBloco, barY, largBloco, barHSup,
                coresSup[i].R, coresSup[i].G, coresSup[i].B);
            compositor.DesenharRetangulo(barX + i * largBloco, barY + barHSup, largBloco, barHInf,
                coresInf[i].R, coresInf[i].G, coresInf[i].B);
        }

        compositor.DesenharRetanguloBorda(barX, barY, totalWidth, barH, 50, 50, 50);

        // Textos via DirectWrite
        string textoTitulo = "NDI DIRECTOR";
        string textoSub = isVertical ? "STANDBY" : "AGUARDANDO FONTES";
        int frameNum = contadorFrames % 30;
        string textoRelogio = DateTime.Now.ToString("HH:mm:ss") + ":" + frameNum.ToString("D2");

        int sizeTitulo = isVertical ? 24 : 32;
        int sizeSub = isVertical ? 14 : 18;
        int sizeRelogio = isVertical ? 16 : 20;

        var (twT, thT) = compositor.MedirTexto(textoTitulo, sizeTitulo);
        var (twS, _) = compositor.MedirTexto(textoSub, sizeSub);
        var (twR, thR) = compositor.MedirTexto(textoRelogio, sizeRelogio);

        // Título no centro
        compositor.DesenharTexto(textoTitulo, sizeTitulo, cx - twT / 2, cy - 15, twT + 10, thT + 10, 240, 240, 240);

        // Subtítulo pulsante
        byte alphaTexto = (byte)(140 + (pulse * 80));
        compositor.DesenharTexto(textoSub, sizeSub, cx - twS / 2, cy + 25, twS + 10, 40, 180, 180, 180, alphaTexto);

        // Timecode SMPTE no rodapé direito
        int rx = (int)(w - twR - (isVertical ? 20 : 40));
        int ry = (int)(h - thR - (isVertical ? 20 : 30));
        compositor.DesenharTexto(textoRelogio, sizeRelogio, rx, ry, twR + 10, thR + 10, 120, 120, 120);

        // STANDBY no rodapé esquerdo
        string textoStandby = "STANDBY";
        int lx = isVertical ? 20 : 40;
        compositor.DesenharTexto(textoStandby, sizeRelogio, lx, ry, 200, thR + 10, 120, 120, 120);

        // Metadados do sinal no canto superior esquerdo
        int sizeMeta = isVertical ? 10 : 12;
        int mx = isVertical ? 20 : 40;
        int my = isVertical ? 20 : 30;
        int espacamento = isVertical ? 14 : 18;
        byte metaAlpha = 140;

        compositor.DesenharTexto("SYNC: INTERNAL", sizeMeta, mx, my, 300, 20, metaAlpha, metaAlpha, metaAlpha, 100);
        compositor.DesenharTexto($"FORMAT: {w}x{h} @ 29.97 FPS", sizeMeta, mx, my + espacamento, 300, 20, metaAlpha, metaAlpha, metaAlpha, 100);
        compositor.DesenharTexto("AUDIO: CH1/CH2 (TEST TONE)", sizeMeta, mx, my + espacamento * 2, 300, 20, metaAlpha, metaAlpha, metaAlpha, 100);
        compositor.DesenharTexto("NDI ENGINE: GPU (D3D11)", sizeMeta, mx, my + espacamento * 3, 300, 20, metaAlpha, metaAlpha, metaAlpha, 100);
    }

    // =====================================================================
    // CÁLCULO DE POSIÇÕES (REUTILIZADA DO VIDEOENGINE ORIGINAL)
    // =====================================================================
    private static Dictionary<string, PosicaoFeed> CalcularPosicoesAlvo(
        List<(string Nome, Mat Frame, string Apelido)> framesAtivos,
        string? nomeHighlight,
        string? nomeSolo,
        int W, int H, int pad)
    {
        var pos = new Dictionary<string, PosicaoFeed>();
        int n = framesAtivos.Count;

        if (AppConfig.MosaicoVertical)
        {
            if (!string.IsNullOrEmpty(nomeSolo))
            {
                var soloFeed = framesAtivos.FirstOrDefault(f => f.Nome == nomeSolo);
                if (soloFeed.Nome != null)
                {
                    pos[nomeSolo] = new PosicaoFeed(pad, pad, W - 2 * pad, H - 2 * pad);
                    return pos;
                }
                else
                {
                    AppConfig.FonteSolo = null;
                }
            }

            if (!string.IsNullOrEmpty(nomeHighlight))
            {
                var hlFeed = framesAtivos.FirstOrDefault(f => f.Nome == nomeHighlight);
                if (hlFeed.Nome != null)
                {
                    if (n == 1)
                    {
                        pos[nomeHighlight] = new PosicaoFeed(pad, pad, W - 2 * pad, H - 2 * pad);
                        return pos;
                    }
                    else
                    {
                        int H_util = H - 2 * pad;
                        int W_esq = (int)(H_util * 16.0 / 9.0);
                        int W_dir = W - 3 * pad - W_esq;
                        if (W_dir < 100)
                        {
                            W_esq = (int)(W * 0.70);
                            W_dir = W - 3 * pad - W_esq;
                        }
                        pos[nomeHighlight] = new PosicaoFeed(pad, pad, W_esq, H_util);

                        var secNomesList = framesAtivos.Select(f => f.Nome).Where(name => name != nomeHighlight).ToList();
                        int nSec = secNomesList.Count;
                        int hBloco = (H_util - (nSec - 1) * pad) / nSec;
                        int pxDir = pad + W_esq + pad;
                        for (int i = 0; i < nSec; i++)
                        {
                            int py = pad + i * (hBloco + pad);
                            pos[secNomesList[i]] = new PosicaoFeed(pxDir, py, W_dir, hBloco);
                        }
                        return pos;
                    }
                }
                else
                {
                    AppConfig.FonteHighlight = null;
                }
            }

            var nomesAtivos = framesAtivos.Select(f => f.Nome).ToList();
            int nAtivo = nomesAtivos.Count;
            if (nAtivo > 0)
            {
                int wSlot = (W - (nAtivo + 1) * pad) / nAtivo;
                int hSlot = H - 2 * pad;
                for (int i = 0; i < nAtivo; i++)
                {
                    int px = pad + i * (wSlot + pad);
                    pos[nomesAtivos[i]] = new PosicaoFeed(px, pad, wSlot, hSlot);
                }
            }
            return pos;
        }

        if (!string.IsNullOrEmpty(nomeSolo))
        {
            var soloFeed = framesAtivos.FirstOrDefault(f => f.Nome == nomeSolo);
            if (soloFeed.Nome != null)
            {
                pos[nomeSolo] = new PosicaoFeed(pad, pad, W - 2 * pad, H - 2 * pad);
                return pos;
            }
            else
            {
                AppConfig.FonteSolo = null;
            }
        }

        string? hlNome = null;
        var secNomes = new List<string>();

        foreach (var feed in framesAtivos)
        {
            if (feed.Nome == nomeHighlight) hlNome = feed.Nome;
            else secNomes.Add(feed.Nome);
        }

        if (!string.IsNullOrEmpty(hlNome))
        {
            if (n == 1)
            {
                pos[hlNome] = new PosicaoFeed(pad, pad, W - 2 * pad, H - 2 * pad);
            }
            else
            {
                int largUtil = W - 3 * pad;
                int wEsq = (int)(largUtil * 0.70);
                int wDir = largUtil - wEsq;
                int hUtil = H - 2 * pad;
                pos[hlNome] = new PosicaoFeed(pad, pad, wEsq, hUtil);

                int nSec = secNomes.Count;
                int hBloco = (hUtil - (nSec - 1) * pad) / nSec;
                int pxDir = pad + wEsq + pad;
                for (int i = 0; i < nSec; i++)
                {
                    int py = pad + i * (hBloco + pad);
                    pos[secNomes[i]] = new PosicaoFeed(pxDir, py, wDir, hBloco);
                }
            }
            return pos;
        }

        var nomes = framesAtivos.Select(f => f.Nome).ToList();
        if (n == 1)
        {
            pos[nomes[0]] = new PosicaoFeed(pad, pad, W - 2 * pad, H - 2 * pad);
        }
        else if (n == 2)
        {
            int wb = (W - 3 * pad) / 2;
            int hb = H - 2 * pad;
            pos[nomes[0]] = new PosicaoFeed(pad, pad, wb, hb);
            pos[nomes[1]] = new PosicaoFeed(pad + wb + pad, pad, wb, hb);
        }
        else if (n == 3)
        {
            int wb = (W - 3 * pad) / 2;
            int hb = (H - 3 * pad) / 2;
            var locs = new[] {
                (pad, pad),
                (pad + wb + pad, pad),
                ((W - wb) / 2, pad + hb + pad)
            };
            for (int i = 0; i < 3; i++)
            {
                pos[nomes[i]] = new PosicaoFeed(locs[i].Item1, locs[i].Item2, wb, hb);
            }
        }
        else
        {
            int wb = (W - 3 * pad) / 2;
            int hb = (H - 3 * pad) / 2;
            var locs = new[] {
                (pad, pad),
                (pad + wb + pad, pad),
                (pad, pad + hb + pad),
                (pad + wb + pad, pad + hb + pad)
            };
            for (int i = 0; i < Math.Min(n, 4); i++)
            {
                pos[nomes[i]] = new PosicaoFeed(locs[i].Item1, locs[i].Item2, wb, hb);
            }
        }

        return pos;
    }
}
