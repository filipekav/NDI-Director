using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Imaging;
using OpenCvSharp;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// MOTOR DE VÍDEO (RUST-LIKE PERFORMANCE EM LERP E COMPOSIÇÃO)
// ===========================================================================
public static class VideoEngine
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

    // Cache de Canvas e Placeholder Preto para alta performance (zero alocações contínuas de CPU)
    private static Mat? _canvasPrincipal;
    private static Mat? _canvasVertical;
    private static readonly Mat FramePretoPlaceholder = new Mat(720, 1280, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));

    private static PrivateFontCollection? _fontCollection;
    private static readonly Dictionary<int, Font> _fontCache = new();

    public static void Iniciar()
    {
        _running = true;
        _engineThread = new Thread(VideoEngineLoop)
        {
            Priority = ThreadPriority.Highest,
            IsBackground = true,
            Name = "NDI_Video_Engine"
        };
        _engineThread.Start();
    }

    public static void Parar()
    {
        _running = false;
        _engineThread?.Join(1000);
    }

    private static Font ObterFonteAnton(int tamanho)
    {
        if (_fontCache.TryGetValue(tamanho, out var font))
            return font;

        if (_fontCollection == null)
        {
            _fontCollection = new PrivateFontCollection();
            var caminhosFonte = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "ANTON-REGULAR.TTF"),           // No output de build/publish (copiado pelo .csproj)
                Path.Combine(AppContext.BaseDirectory, "..\\assets\\ANTON-REGULAR.TTF"), // Desenvolvimento (dotnet run dentro de src/)
                Path.Combine(AppContext.BaseDirectory, "..\\..\\assets\\ANTON-REGULAR.TTF"),
                Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\assets\\ANTON-REGULAR.TTF"),
                "assets\\ANTON-REGULAR.TTF"
            };

            string? fontPath = caminhosFonte.FirstOrDefault(File.Exists);

            if (!string.IsNullOrEmpty(fontPath))
            {
                try
                {
                    _fontCollection.AddFontFile(fontPath);
                    if (_fontCollection.Families.Length > 0)
                    {
                        Console.WriteLine($"[*] Fonte ANTON carregada com sucesso de: {fontPath} (Família: {_fontCollection.Families[0].Name})");
                    }
                    else
                    {
                        Console.WriteLine($"[!] Erro: Fonte ANTON lida de {fontPath}, mas nao expoe familias no PrivateFontCollection.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao adicionar fonte ANTON do arquivo {fontPath}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("[!] Erro fatal: Arquivo ANTON-REGULAR.TTF nao foi encontrado nas proximidades!");
            }
        }

        Font novaFont;
        if (_fontCollection.Families.Length > 0)
        {
            novaFont = new Font(_fontCollection.Families[0], tamanho, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        else
        {
            novaFont = new Font("Arial", tamanho, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        _fontCache[tamanho] = novaFont;
        return novaFont;
    }

    private static unsafe void VideoEngineLoop()
    {
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

        Console.WriteLine("[*] Outputs NDI 'MESA_NDI_MOSAICO', 'MESA_NDI_VERTICAL' e 'MESA_NDI_AUDIO' inicializados.");

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

        // Inicializa os canvas estáticos persistentes
        _canvasPrincipal = new Mat(currentH, currentW, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        _canvasVertical = new Mat(currentHV, currentWV, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));

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
                _canvasPrincipal?.Dispose();
                _canvasPrincipal = new Mat(currentH, currentW, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                
                videoFrame.xres = currentW;
                videoFrame.yres = currentH;
                videoFrame.line_stride_in_bytes = currentW * 4;
                videoFrame.picture_aspect_ratio = (float)currentW / currentH;

                // Limpa posições interpoladas anteriores para evitar crash de ROI fora dos limites do novo canvas
                _posicoesAtuais.Clear();
            }

            if (AppConfig.CanvasLarguraVertical != currentWV || AppConfig.CanvasAlturaVertical != currentHV)
            {
                currentWV = AppConfig.CanvasLarguraVertical;
                currentHV = AppConfig.CanvasAlturaVertical;
                _canvasVertical?.Dispose();
                _canvasVertical = new Mat(currentHV, currentWV, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
                
                videoFrameV.xres = currentWV;
                videoFrameV.yres = currentHV;
                videoFrameV.line_stride_in_bytes = currentWV * 4;
                videoFrameV.picture_aspect_ratio = (float)currentWV / currentHV;
            }

            var framesAtivos = new List<(string Nome, Mat Frame, string Apelido)>();

            lock (AppConfig.LockFontes)
            {
                for (int i = 0; i < 4; i++)
                {
                    string? nome = AppConfig.OrdemReceptores[i];
                    if (!string.IsNullOrEmpty(nome) && AppConfig.ReceptoresAtivos.TryGetValue(nome, out var rec))
                    {
                        // Usa a referência do placeholder sem alocar nova Mat na CPU
                        var frame = rec.ObterFrame() ?? new Mat(FramePretoPlaceholder, new Rect(0, 0, FramePretoPlaceholder.Width, FramePretoPlaceholder.Height));
                        string apelido = AppConfig.ApelidosFontes.TryGetValue(nome, out var ap) ? ap : "";
                        framesAtivos.Add((nome, frame, apelido));
                    }
                }
            }

            Scalar bgScalar;
            lock (AppConfig.LockFontes)
            {
                var col = AppConfig.CoresBackground[AppConfig.CorFundoAtual];
                bgScalar = new Scalar(col.B, col.G, col.R, col.A);
            }

            // Timecode lógico comum de alta precisão em ticks (100ns)
            long timecodeComum = DateTime.UtcNow.Ticks;

            // -------------------------------------------------------------
            // Renderiza Canvas Principal
            // -------------------------------------------------------------
            var canvas = _canvasPrincipal;
            canvas.SetTo(bgScalar);

            if (framesAtivos.Count == 0)
            {
                _posicoesAtuais.Clear();
                Cv2.PutText(canvas, "Aguardando Fontes...", new OpenCvSharp.Point(700, 540),
                    HersheyFonts.HersheySimplex, 1.5, new Scalar(100, 100, 100, 255), 3, LineTypes.AntiAlias);
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
                        int x = (int)Math.Round(pos.X);
                        int y = (int)Math.Round(pos.Y);
                        int mw = (int)Math.Round(pos.W);
                        int mh = (int)Math.Round(pos.H);

                        if (mw > 0 && mh > 0)
                        {
                            int fontSize = 32;
                            if (mw > 1000) fontSize = 44;
                            else if (mw < 600) fontSize = 24;

                            var fontGC = ObterFonteAnton(fontSize);
                            DesenharComAspectRatio(canvas, feed.Frame, x, y, mw, mh, feed.Apelido, fontGC, AppConfig.MosaicoVertical && (AppConfig.FonteHighlight != nome));
                        }
                    }
                }
            }

            videoFrame.p_data = canvas.Data;
            videoFrame.timecode = timecodeComum;
            NDIlib.send_send_video_v2(pNdiSend, ref videoFrame);
            _contadorFramesMosaico++;

            // -------------------------------------------------------------
            // Renderiza Canvas Vertical
            // -------------------------------------------------------------
            var canvasV = _canvasVertical;
            canvasV.SetTo(bgScalar);

            if (framesAtivos.Count == 0)
            {
                Cv2.PutText(canvasV, "Aguardando...", new OpenCvSharp.Point(60, currentHV / 2),
                    HersheyFonts.HersheySimplex, 1.0, new Scalar(100, 100, 100, 255), 2, LineTypes.AntiAlias);
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

                    int fontSize = 32;
                    int mw = currentWV - 2 * padV;
                    if (mw < 600) fontSize = 24;

                    var fontGC = ObterFonteAnton(fontSize);
                    DesenharComAspectRatio(canvasV, feed.Frame, padV, py, mw, hBloco, feed.Apelido, fontGC);
                }
            }

            videoFrameV.p_data = canvasV.Data;
            videoFrameV.timecode = timecodeComum;
            NDIlib.send_send_video_v2(pNdiSendV, ref videoFrameV);
            _contadorFramesVertical++;

            // -------------------------------------------------------------
            // Envia áudio mixado acumulado no mixer
            // -------------------------------------------------------------
            while (AppConfig.MixerGlobal.FilaSaida.TryDequeue(out float[]? blocoAudio))
            {
                if (AppConfig.HabilitarLogsDiagnostico)
                {
                    Console.WriteLine($"[DEBUG-AUDIO] Enviando bloco de audio mixado via NDI. Samples={AudioMixer.TamanhoBloco}");
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
        Marshal.FreeHGlobal(pAudioBufferNativo);
    }

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

    private static void DesenharComAspectRatio(
        Mat canvas, Mat frame, int x, int y, int maxW, int maxH,
        string? textoExibicao, Font fontGC, bool cropVertical = false)
    {
        int w = frame.Width;
        int h = frame.Height;
        
        int novoW, novoH;
        int offsetX, offsetY;
        var frameRedim = new Mat();
        
        if (cropVertical && maxW > 0 && maxH > 0)
        {
            double targetAr = (double)maxW / maxH;
            double srcAr = (double)w / h;
            
            int cropX = 0, cropY = 0;
            int cropW = w, cropH = h;
            
            if (srcAr > targetAr)
            {
                cropW = (int)Math.Round(h * targetAr);
                cropX = (w - cropW) / 2;
            }
            else
            {
                cropH = (int)Math.Round(w / targetAr);
                cropY = (h - cropH) / 2;
            }
            
            cropX = Math.Max(0, Math.Min(cropX, w - 1));
            cropY = Math.Max(0, Math.Min(cropY, h - 1));
            cropW = Math.Max(1, Math.Min(cropW, w - cropX));
            cropH = Math.Max(1, Math.Min(cropH, h - cropY));
            
            using (var cropped = new Mat(frame, new Rect(cropX, cropY, cropW, cropH)))
            {
                Cv2.Resize(cropped, frameRedim, new OpenCvSharp.Size(maxW, maxH), 0, 0, InterpolationFlags.Linear);
            }
            
            novoW = maxW;
            novoH = maxH;
            offsetX = x;
            offsetY = y;
        }
        else
        {
            double escala = Math.Min((double)maxW / w, (double)maxH / h);
            novoW = (int)(w * escala);
            novoH = (int)(h * escala);

            if (novoW <= 0 || novoH <= 0) return;

            Cv2.Resize(frame, frameRedim, new OpenCvSharp.Size(novoW, novoH), 0, 0, InterpolationFlags.Linear);

            offsetX = x + (maxW - novoW) / 2;
            offsetY = y + (maxH - novoH) / 2;
        }

        // Validação defensiva: garante que a região de interesse (ROI) não ultrapasse os limites físicos do canvas.
        // Se ultrapassar, descartamos a renderização deste feed neste frame para evitar que o NDI Director crashe.
        if (offsetX < 0 || offsetY < 0 || novoW <= 0 || novoH <= 0 || 
            (offsetX + novoW) > canvas.Cols || (offsetY + novoH) > canvas.Rows)
        {
            if (AppConfig.HabilitarLogsDiagnostico)
            {
                Console.WriteLine($"[AVISO-VIDEO] Ignorando renderizacao de feed fora dos limites. Canvas: {canvas.Cols}x{canvas.Rows}, ROI: x={offsetX}, y={offsetY}, w={novoW}, h={novoH}");
            }
            frameRedim.Dispose();
            return;
        }

        using (var roi = new Mat(canvas, new Rect(offsetX, offsetY, novoW, novoH)))
        {
            if (frameRedim.Channels() == 4)
            {
                frameRedim.CopyTo(roi);
            }
            else
            {
                using var tempBg = new Mat();
                Cv2.CvtColor(frameRedim, tempBg, ColorConversionCodes.BGR2BGRA);
                tempBg.CopyTo(roi);
            }
        }
        
        frameRedim.Dispose();

        if (!string.IsNullOrEmpty(textoExibicao))
        {
            DesenharGC(canvas, offsetX, offsetY, novoW, novoH, textoExibicao, fontGC);
        }
    }

    // Cache de tamanhos de texto medidos para evitar criação contínua de Bitmaps/Graphics de medição (ótimo para CPU)
    private static readonly Dictionary<(string Texto, float Size), (int W, int H)> _textSizeCache = new();

    private static (int W, int H) ObterTamanhoTexto(string texto, Font font)
    {
        var key = (texto, font.Size);
        if (_textSizeCache.TryGetValue(key, out var tam))
            return tam;

        // Prevenção contra Vazamento de Memória: limpa o cache se crescer demais (acima de 1000 chaves)
        if (_textSizeCache.Count > 1000)
        {
            _textSizeCache.Clear();
        }

        int tw, th;
        using (var imgMedir = new Bitmap(1, 1))
        using (var gMedir = Graphics.FromImage(imgMedir))
        {
            var size = gMedir.MeasureString(texto, font);
            tw = (int)Math.Ceiling(size.Width);
            th = (int)Math.Ceiling(size.Height);
        }

        var resultado = (tw, th);
        _textSizeCache[key] = resultado;
        return resultado;
    }

    private static void DesenharGC(Mat canvas, int offsetX, int offsetY, int novoW, int novoH, string texto, Font font)
    {
        var (tw, th) = ObterTamanhoTexto(texto, font);

        int paddingX = 18;
        int paddingV = 10;
        int alturaBarra = th + paddingV * 2;

        int gcX1 = offsetX;
        int gcY1 = offsetY + novoH - alturaBarra;
        int gcX2 = Math.Min(offsetX + tw + 2 * paddingX, offsetX + novoW);
        int gcY2 = offsetY + novoH;

        int barW = gcX2 - gcX1;
        int barH = Math.Max(1, gcY2 - gcY1);

        if (barW <= 0 || barH <= 0 || gcY1 < 0 || gcY2 > canvas.Height || gcX1 < 0 || gcX2 > canvas.Width)
            return;

        using var roiBarra = new Mat(canvas, new Rect(gcX1, gcY1, barW, barH));
        using var fundo = new Mat(barH, barW, MatType.CV_8UC4, new Scalar(4, 0, 84, 255));
        using var blended = new Mat();
        Cv2.AddWeighted(fundo, 0.85, roiBarra, 0.15, 0, blended);
        blended.CopyTo(roiBarra);

        using (var bmp = new Bitmap(barW, barH, (int)roiBarra.Step(), PixelFormat.Format32bppArgb, roiBarra.Data))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var brush = new SolidBrush(Color.White);
            g.DrawString(texto, font, brush, paddingX, paddingV);
        }
    }
}
