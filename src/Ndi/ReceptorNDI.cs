using System.Runtime.InteropServices;
using OpenCvSharp;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// CLASSE DO RECEPTOR NDI
// ===========================================================================
public class ReceptorNDI
{
    public string Nome { get; }
    public bool LowBandwidth { get; }
    public Mat? FrameAtual { get; private set; }
    public bool Erro { get; private set; }
    public int XRes { get; private set; } = 0;
    public int YRes { get; private set; } = 0;
    public double Fps { get; private set; } = 0.0;

    // Estatísticas de Performance da Conexão NDI
    public long VideoFrames { get; private set; } = 0;
    public long AudioFrames { get; private set; } = 0;
    public long DroppedVideoFrames { get; private set; } = 0;
    public long DroppedAudioFrames { get; private set; } = 0;
    
    private IntPtr _pRecv = IntPtr.Zero;
    private Thread? _threadCapture;
    private bool _running = false;
    private readonly object _frameLock = new();
    private DateTime _lastFrameTime = DateTime.MinValue;

    // Buffers persistentes para evitar alocações constantes de memória heap nativa
    private Mat? _bufferA;
    private Mat? _bufferB;
    private bool _useBufferA = true;

    public ReceptorNDI(string nome, bool lowBandwidth = false)
    {
        Nome = nome;
        LowBandwidth = lowBandwidth;
        _running = true;
        _lastFrameTime = DateTime.Now; // Inicializa com a hora atual para dar tempo de capturar o primeiro frame
        _threadCapture = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"Capture_{nome}"
        };
        _threadCapture.Start();
    }

    private void CaptureLoop()
    {
        var source = new NDIlib.source_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi(Nome)
        };

        var recvSettings = new NDIlib.recv_create_v3_t
        {
            source_to_connect_to = source,
            color_format = NDIlib.recv_color_format_e.recv_color_format_BGRX_BGRA,
            bandwidth = LowBandwidth ? NDIlib.recv_bandwidth_e.recv_bandwidth_lowest : NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
            allow_video_fields = false
        };

        _pRecv = NDIlib.recv_create_v3(ref recvSettings);
        Marshal.FreeHGlobal(source.p_ndi_name);

        if (_pRecv == IntPtr.Zero)
        {
            Erro = true;
            Console.WriteLine($"[!] Falha ao criar receptor para: {Nome}");
            return;
        }

        var videoFrame = new NDIlib.video_frame_v2_t();
        var audioFrame = new NDIlib.audio_frame_v3_t();
        var metadataFrame = new NDIlib.metadata_frame_t();

        while (_running)
        {
            if (_pRecv != IntPtr.Zero)
            {
                NDIlib.recv_performance_t perfTotal = new NDIlib.recv_performance_t();
                NDIlib.recv_performance_t perfDropped = new NDIlib.recv_performance_t();
                NDIlib.recv_get_performance(_pRecv, ref perfTotal, ref perfDropped);
                
                VideoFrames = perfTotal.video_frames;
                AudioFrames = perfTotal.audio_frames;
                DroppedVideoFrames = perfDropped.video_frames;
                DroppedAudioFrames = perfDropped.audio_frames;
            }

            NDIlib.frame_type_e frameType = NDIlib.recv_capture_v3(_pRecv, ref videoFrame, ref audioFrame, ref metadataFrame, 200);
            bool erroAntes = Erro;
            bool resAlterada = false;
            
            if (frameType == NDIlib.frame_type_e.frame_type_video)
            {
                if (videoFrame.p_data != IntPtr.Zero && videoFrame.xres > 0 && videoFrame.yres > 0)
                {
                    using var rawMat = Mat.FromPixelData(videoFrame.yres, videoFrame.xres, MatType.CV_8UC4, videoFrame.p_data, videoFrame.line_stride_in_bytes);
                    
                    lock (_frameLock)
                    {
                        double calculoFps = 0.0;
                        if (videoFrame.frame_rate_D > 0)
                        {
                            calculoFps = Math.Round((double)videoFrame.frame_rate_N / videoFrame.frame_rate_D, 2);
                        }

                        if (XRes != videoFrame.xres || YRes != videoFrame.yres || Fps != calculoFps)
                        {
                            XRes = videoFrame.xres;
                            YRes = videoFrame.yres;
                            Fps = calculoFps;
                            resAlterada = true;
                        }

                        // Obtém o buffer traseiro que não está sendo lido atualmente
                        Mat? backBuffer = _useBufferA ? _bufferB : _bufferA;

                        // Se o tamanho mudar ou for a primeira execução, (re)inicializa o buffer correspondente
                        if (backBuffer == null || backBuffer.Width != videoFrame.xres || backBuffer.Height != videoFrame.yres)
                        {
                            backBuffer?.Dispose();
                            backBuffer = new Mat(videoFrame.yres, videoFrame.xres, MatType.CV_8UC4);
                            if (_useBufferA) _bufferB = backBuffer; else _bufferA = backBuffer;
                        }

                        // Cópia direta e rápida de blocos de memória
                        rawMat.CopyTo(backBuffer);
                        
                        // Expõe o buffer atualizado como FrameAtual
                        FrameAtual = backBuffer;

                        // Alterna para o outro buffer para a próxima escrita
                        _useBufferA = !_useBufferA;
                        
                        _lastFrameTime = DateTime.Now;
                        Erro = false;
                    }

                    // Gravação do frame original em tempo real por FFmpeg e NVIDIA GPU
                    GravadorFFmpeg? gravador;
                    lock (AppConfig.LockGravadores)
                    {
                        AppConfig.GravadoresAtivos.TryGetValue(Nome, out gravador);
                    }
                    if (gravador != null)
                    {
                        if (!gravador.Gravando)
                        {
                            gravador.Iniciar(videoFrame.xres, videoFrame.yres, videoFrame.frame_rate_N, videoFrame.frame_rate_D);
                        }
                        gravador.EscreverFrame(rawMat);
                    }
                }
                NDIlib.recv_free_video_v2(_pRecv, ref videoFrame);
            }
            else if (frameType == NDIlib.frame_type_e.frame_type_audio)
            {
                if (!LowBandwidth && audioFrame.p_data != IntPtr.Zero && audioFrame.no_channels > 0 && audioFrame.no_samples > 0)
                {
                    AppConfig.MixerGlobal.AdicionarAudio(Nome, audioFrame);

                    GravadorFFmpeg? gravador;
                    lock (AppConfig.LockGravadores)
                    {
                        AppConfig.GravadoresAtivos.TryGetValue(Nome, out gravador);
                    }
                    if (gravador != null && gravador.Gravando)
                    {
                        gravador.EscreverAudio(audioFrame);
                    }
                }
                NDIlib.recv_free_audio_v3(_pRecv, ref audioFrame);
            }
            else if (frameType == NDIlib.frame_type_e.frame_type_metadata)
            {
                NDIlib.recv_free_metadata(_pRecv, ref metadataFrame);
            }
            else if (frameType == NDIlib.frame_type_e.frame_type_error)
            {
                Erro = true;
            }
            
            if (DateTime.Now - _lastFrameTime > TimeSpan.FromSeconds(3))
            {
                Erro = true;
            }

            // Notifica o frontend imediatamente na mudança de status (reconectando/conectado) ou na resolução
            if (Erro != erroAntes || resAlterada)
            {
                if (Erro)
                {
                    lock (_frameLock)
                    {
                        XRes = 0;
                        YRes = 0;
                        Fps = 0.0;
                    }
                }
                if (Erro != erroAntes)
                {
                    Console.WriteLine(Erro 
                        ? $"[~] Perda de sinal detectada para: {Nome}" 
                        : $"[+] Conexao estabelecida / frame recebido para: {Nome}");
                }
                SseManager.NotificarClientes();
            }
        }

        lock (_frameLock)
        {
            FrameAtual = null;
            _bufferA?.Dispose();
            _bufferA = null;
            _bufferB?.Dispose();
            _bufferB = null;
            XRes = 0;
            YRes = 0;
            Fps = 0.0;
        }
        
        if (_pRecv != IntPtr.Zero)
        {
            NDIlib.recv_destroy(_pRecv);
        }
    }

    public Mat? ObterFrame()
    {
        lock (_frameLock)
        {
            if (FrameAtual == null || FrameAtual.IsDisposed) return null;
            // Cria um sub-header Mat que aponta para os mesmos pixels do buffer frontal ativo (referência nativa rápida e segura)
            return new Mat(FrameAtual, new Rect(0, 0, FrameAtual.Width, FrameAtual.Height));
        }
    }

    public void Parar()
    {
        _running = false;
        _threadCapture?.Join(1000);
        lock (_frameLock)
        {
            _bufferA?.Dispose();
            _bufferA = null;
            _bufferB?.Dispose();
            _bufferB = null;
            FrameAtual = null;
            XRes = 0;
            YRes = 0;
            Fps = 0.0;
        }
    }
}
