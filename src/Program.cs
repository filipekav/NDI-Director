using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Imaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenCvSharp;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// CONFIGURAÇÃO E ESTADO GLOBAL
// ===========================================================================
public static class AppConfig
{
    public static List<string> FontesNaRede = new();
    public static Dictionary<string, ReceptorNDI> ReceptoresAtivos = new();
    public static string?[] OrdemReceptores = new string?[4];
    public static string? FonteHighlight = null;
    public static string? FonteSolo = null;
    public static Dictionary<string, string> ApelidosFontes = new();
    public static string CorFundoAtual = "verde";
    public static string FormatoAudioAtual = "aac"; // "pcm" ou "aac"
    public static bool ApagarTemporarios = true;
    public static string QualidadeGravacao = "media"; // "alta", "media" ou "baixa"
    public static bool HabilitarLogsDiagnostico = false; // Silencia por padrão logs verbosos de progresso e sincronia
    public static bool MosaicoVertical = false;
    public static int PaddingMosaico = 20;
    public static Dictionary<string, float> VolumesFontes = new();
    public static Dictionary<string, int> NiveisVu = new();
    
    public static readonly object LockFontes = new();
    public static readonly object LockVolumes = new();
    public static readonly object LockVu = new();
    
    // Gravadores individuais por FFmpeg acelerado por NVIDIA GPU
    public static Dictionary<string, GravadorFFmpeg> GravadoresAtivos = new();
    public static readonly object LockGravadores = new();
    
    // Muxing em andamento
    public static Dictionary<string, MuxingStatus> ProcessosMuxing = new();
    public static readonly object LockMuxing = new();
    public static readonly Dictionary<string, (byte R, byte G, byte B, byte A)> CoresBackground = new()
    {
        { "cinza", (15, 15, 15, 255) },
        { "verde", (0, 255, 0, 255) },
        { "azul", (0, 0, 255, 255) },
        { "preto", (0, 0, 0, 255) },
        { "transparente", (0, 0, 0, 0) }
    };

    public static readonly AudioMixer MixerGlobal = new();
}

public class MuxingStatus
{
    public string NomeFonte { get; set; } = "";
    public double Progresso { get; set; } = 0;
    public bool Concluido { get; set; } = false;
    public string? Erro { get; set; }
}

// ===========================================================================
// MIXER DE ÁUDIO NDI EM TEMPO REAL
// ===========================================================================
public class AudioMixer
{
    public const int SampleRateSaida = 48000;
    public const int CanaisSaida = 2;
    public const int TamanhoBloco = 960; // 20ms de áudio a 48kHz
    private const double IntervaloBlocoMs = 20.0;

    private readonly Dictionary<string, (Queue<float> L, Queue<float> R)> _buffers = new();
    private readonly object _lockBuffers = new();

    // Fila de blocos mixados prontos para a saída NDI (contém arrays de float de tamanho 2 * 960 = 1920)
    public readonly System.Collections.Concurrent.ConcurrentQueue<float[]> FilaSaida = new();

    private Thread? _mixerThread;
    private bool _running = false;

    public void Iniciar()
    {
        if (_running) return;
        _running = true;
        _mixerThread = new Thread(MixerLoop)
        {
            IsBackground = true,
            Name = "NDI_Audio_Mixer",
            Priority = ThreadPriority.AboveNormal
        };
        _mixerThread.Start();
        Console.WriteLine("[*] Mixer de áudio NDI iniciado com sucesso (48kHz, Estéreo).");
    }

    public void Parar()
    {
        _running = false;
        _mixerThread?.Join(1000);
        lock (_lockBuffers)
        {
            _buffers.Clear();
        }
        while (FilaSaida.TryDequeue(out _)) { }
    }

    public unsafe void AdicionarAudio(string nomeFonte, NDIlib.audio_frame_v3_t audioFrame)
    {
        if (!_running) return;

        try
        {
            int noChannels = audioFrame.no_channels;
            int noSamples = audioFrame.no_samples;
            int stride = audioFrame.channel_stride_in_bytes;
            int sampleRate = audioFrame.sample_rate;

            if (noChannels <= 0 || noSamples <= 0 || audioFrame.p_data == IntPtr.Zero) return;

            // 1. Extração e normalização para estéreo
            if (AppConfig.HabilitarLogsDiagnostico)
            {
                Console.WriteLine($"[DEBUG-AUDIO] AdicionarAudio de {nomeFonte}: channels={noChannels}, samples={noSamples}, rate={sampleRate}");
            }
            float[] left = new float[noSamples];
            float[] right = new float[noSamples];

            byte* pSrcBase = (byte*)audioFrame.p_data.ToPointer();

            if (noChannels == 1)
            {
                // Mono: Copia o único canal para L e R
                float* pSrc = (float*)pSrcBase;
                for (int i = 0; i < noSamples; i++)
                {
                    left[i] = pSrc[i];
                    right[i] = pSrc[i];
                }
            }
            else
            {
                // Estéreo ou Multicanal: Pega os dois primeiros canais
                float* pSrcL = (float*)pSrcBase;
                float* pSrcR = (float*)(pSrcBase + stride);
                for (int i = 0; i < noSamples; i++)
                {
                    left[i] = pSrcL[i];
                    right[i] = pSrcR[i];
                }
            }

            // 2. Reamostragem (Resampling) linear rápida para 48kHz se necessário
            float[] leftResampled;
            float[] rightResampled;

            if (sampleRate != SampleRateSaida)
            {
                int numAmostrasOut = (int)Math.Round((double)noSamples * SampleRateSaida / sampleRate);
                if (numAmostrasOut <= 0) return;

                leftResampled = new float[numAmostrasOut];
                rightResampled = new float[numAmostrasOut];

                double ratio = (double)(noSamples - 1) / (numAmostrasOut - 1);
                for (int i = 0; i < numAmostrasOut; i++)
                {
                    double srcIdx = i * ratio;
                    int idxLow = (int)Math.Floor(srcIdx);
                    int idxHigh = (int)Math.Ceiling(srcIdx);
                    double weight = srcIdx - idxLow;

                    idxLow = Math.Max(0, Math.Min(idxLow, noSamples - 1));
                    idxHigh = Math.Max(0, Math.Min(idxHigh, noSamples - 1));

                    leftResampled[i] = (float)((1.0 - weight) * left[idxLow] + weight * left[idxHigh]);
                    rightResampled[i] = (float)((1.0 - weight) * right[idxLow] + weight * right[idxHigh]);
                }
            }
            else
            {
                leftResampled = left;
                rightResampled = right;
            }

            // 3. Adicionar aos buffers circulares
            lock (_lockBuffers)
            {
                if (!_buffers.TryGetValue(nomeFonte, out var deques))
                {
                    deques = (new Queue<float>(480000), new Queue<float>(480000));
                    _buffers[nomeFonte] = deques;
                }

                int maxCapacity = 480000;
                for (int i = 0; i < leftResampled.Length; i++)
                {
                    if (deques.L.Count >= maxCapacity)
                    {
                        deques.L.Dequeue();
                        deques.R.Dequeue();
                    }
                    deques.L.Enqueue(leftResampled[i]);
                    deques.R.Enqueue(rightResampled[i]);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Erro ao processar áudio no mixer para a fonte '{nomeFonte}': {ex.Message}");
        }
    }

    private (float[] L, float[] R) ObterAmostrasFonte(string nomeFonte, int quantidade)
    {
        float[] leftSamples = new float[quantidade];
        float[] rightSamples = new float[quantidade];

        lock (_lockBuffers)
        {
            if (_buffers.TryGetValue(nomeFonte, out var deques))
            {
                int disponivel = deques.L.Count;
                int tamanhoLer = Math.Min(quantidade, disponivel);
                for (int i = 0; i < tamanhoLer; i++)
                {
                    leftSamples[i] = deques.L.Dequeue();
                    rightSamples[i] = deques.R.Dequeue();
                }
            }
        }

        return (leftSamples, rightSamples);
    }

    private void MixerLoop()
    {
        double proximaExecucao = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;

        while (_running)
        {
            double agora = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
            if (agora < proximaExecucao)
            {
                double tempoEspera = proximaExecucao - agora;
                Thread.Sleep((int)Math.Max(1, tempoEspera));
                continue;
            }

            proximaExecucao += IntervaloBlocoMs;

            // Obtém fontes ativas na matriz
            var fontesAtivas = new List<string>();
            lock (AppConfig.LockFontes)
            {
                for (int i = 0; i < 4; i++)
                {
                    string? nome = AppConfig.OrdemReceptores[i];
                    if (!string.IsNullOrEmpty(nome) && AppConfig.ReceptoresAtivos.ContainsKey(nome))
                    {
                        fontesAtivas.Add(nome);
                    }
                }
            }

            if (fontesAtivas.Count == 0)
            {
                lock (_lockBuffers)
                {
                    _buffers.Clear();
                }
                lock (AppConfig.LockVu)
                {
                    AppConfig.NiveisVu.Clear();
                }
                continue;
            }

            // Mixa amostras
            float[] somaL = new float[TamanhoBloco];
            float[] somaR = new float[TamanhoBloco];

            lock (_lockBuffers)
            {
                // Remove de buffers fontes que não estão mais ativas
                var chavesRemover = _buffers.Keys.Where(k => !fontesAtivas.Contains(k)).ToList();
                foreach (var k in chavesRemover)
                {
                    _buffers.Remove(k);
                }

                lock (AppConfig.LockVu)
                {
                    var chavesVuRemover = AppConfig.NiveisVu.Keys.Where(k => !fontesAtivas.Contains(k)).ToList();
                    foreach (var k in chavesVuRemover)
                    {
                        AppConfig.NiveisVu.Remove(k);
                    }
                }

                foreach (var nome in fontesAtivas)
                {
                    // Obtém volume (padrão 1.0f)
                    float ganho = 1.0f;
                    lock (AppConfig.LockVolumes)
                    {
                        if (AppConfig.VolumesFontes.TryGetValue(nome, out float v))
                        {
                            ganho = v;
                        }
                    }

                    var (left, right) = ObterAmostrasFonte(nome, TamanhoBloco);

                    // VU Meter: Pico absoluto das amostras brutas (para oscilar mesmo mutado)
                    float pico = 0f;
                    for (int i = 0; i < TamanhoBloco; i++)
                    {
                        float absL = Math.Abs(left[i]);
                        float absR = Math.Abs(right[i]);
                        if (absL > pico) pico = absL;
                        if (absR > pico) pico = absR;
                    }

                    int novoVu = 0;
                    if (pico > 0.00003f)
                    {
                        double db = 20 * Math.Log10(pico);
                        novoVu = (int)Math.Max(0, Math.Min(100, ((db + 50) / 50) * 100));
                    }

                    lock (AppConfig.LockVu)
                    {
                        AppConfig.NiveisVu.TryGetValue(nome, out int nivelAnterior);
                        int nivelVu;
                        if (novoVu < nivelAnterior)
                        {
                            nivelVu = (int)Math.Max(novoVu, nivelAnterior * 0.92);
                        }
                        else
                        {
                            nivelVu = novoVu;
                        }
                        AppConfig.NiveisVu[nome] = nivelVu;
                    }

                    if (ganho > 0.001f)
                    {
                        for (int i = 0; i < TamanhoBloco; i++)
                        {
                            somaL[i] += left[i] * ganho;
                            somaR[i] += right[i] * ganho;
                        }
                    }
                }
            }

            // Clip entre -1.0f e 1.0f
            for (int i = 0; i < TamanhoBloco; i++)
            {
                if (somaL[i] > 1.0f) somaL[i] = 1.0f;
                else if (somaL[i] < -1.0f) somaL[i] = -1.0f;

                if (somaR[i] > 1.0f) somaR[i] = 1.0f;
                else if (somaR[i] < -1.0f) somaR[i] = -1.0f;
            }

            // Monta buffer planar (Canal 0 primeiro, depois Canal 1)
            float[] blocoMixado = new float[TamanhoBloco * 2];
            Array.Copy(somaL, 0, blocoMixado, 0, TamanhoBloco);
            Array.Copy(somaR, 0, blocoMixado, TamanhoBloco, TamanhoBloco);

            if (FilaSaida.Count < 100)
            {
                FilaSaida.Enqueue(blocoMixado);
            }
            else
            {
                FilaSaida.TryDequeue(out _);
                FilaSaida.Enqueue(blocoMixado);
            }
        }
    }
}

// ===========================================================================
// CLASSE DO RECEPTOR NDI
// ===========================================================================
public class ReceptorNDI
{
    public string Nome { get; }
    public Mat? FrameAtual { get; private set; }
    public bool Erro { get; private set; }
    public int XRes { get; private set; } = 0;
    public int YRes { get; private set; } = 0;
    public double Fps { get; private set; } = 0.0;
    
    private IntPtr _pRecv = IntPtr.Zero;
    private Thread? _threadCapture;
    private bool _running = false;
    private readonly object _frameLock = new();
    private DateTime _lastFrameTime = DateTime.MinValue;

    // Buffers persistentes para evitar alocações constantes de memória heap nativa
    private Mat? _bufferA;
    private Mat? _bufferB;
    private bool _useBufferA = true;

    public ReceptorNDI(string nome)
    {
        Nome = nome;
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
            bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
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
                if (audioFrame.p_data != IntPtr.Zero && audioFrame.no_channels > 0 && audioFrame.no_samples > 0)
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

// ===========================================================================
// CLASSE DE GRAVAÇÃO DO PARTICIPANTE VIA FFMEG E NVIDIA GPU (NVENC)
// ===========================================================================
public class GravadorFFmpeg
{
    public string NomeFonte { get; }
    public string CaminhoArquivo { get; }
    public bool Gravando { get; private set; }
    public DateTime? TempoInicioGravacao => _tempoInicioGravacao;

    private readonly object _lock = new();
    private int _width;
    private int _height;
    private int _sampleRate = 48000;
    private int _channels = 2;

    // --- Configuração de FPS e temporização para gravação CFR ---
    private int _frameRateN = 30000;
    private int _frameRateD = 1001;
    private double _frameIntervalMs = 33.366;
    private byte[]? _ultimoFrameBytes;
    private readonly object _frameLock = new();
    private DateTime? _tempoInicioGravacao;
    private long _totalAmostrasGravadas = 0;

    // --- Processo de vídeo (stdin -> arquivo temporário de vídeo) ---
    private System.Diagnostics.Process? _procVideo;
    private Stream? _stdinVideo;
    private string? _caminhoVideoTemp;

    // --- Thread de vídeo ---
    private Thread? _videoWriterThread;

    // --- Processo de áudio ---
    private System.Diagnostics.Process? _procAudio;
    private Stream? _stdinAudio;
    private string? _caminhoAudioTemp;
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _audioQueue = new();
    private Thread? _audioWriterThread;

    private string _formatoAudio = "pcm";
    private bool _rodandoThreads = false;

    public GravadorFFmpeg(string nomeFonte, string caminhoArquivo, string formatoAudio)
    {
        NomeFonte = nomeFonte;
        CaminhoArquivo = caminhoArquivo;
        _formatoAudio = formatoAudio;
    }

    public void Iniciar(int width, int height, int frameRateN = 30000, int frameRateD = 1001)
    {
        lock (_lock)
        {
            if (Gravando) return;
            Gravando = true;
            _rodandoThreads = true;

            try
            {
                // Garante que a resolução é divisível por 2 (requisito obrigatório para codificação H.264/NVENC)
                _width = width & ~1;
                _height = height & ~1;

                // Configura e valida FPS
                if (frameRateN > 0 && frameRateD > 0)
                {
                    _frameRateN = frameRateN;
                    _frameRateD = frameRateD;
                }
                else
                {
                    _frameRateN = 30000;
                    _frameRateD = 1001;
                }
                _frameIntervalMs = (double)_frameRateD * 1000.0 / _frameRateN;
                _ultimoFrameBytes = null;
                _tempoInicioGravacao = null;
                _totalAmostrasGravadas = 0;

                // Limpa as filas de gravação de áudio
                while (_audioQueue.TryDequeue(out _) ) { }

                // Arquivos temporários na pasta Downloads (mesmo local do arquivo final)
                string tempDir = Path.GetDirectoryName(CaminhoArquivo) ?? Path.GetTempPath();
                string tempBase = Path.Combine(tempDir, $"_ndi_temp_{Guid.NewGuid():N}");
                _caminhoVideoTemp = tempBase + "_video.mp4";

                if (_formatoAudio == "aac")
                {
                    _caminhoAudioTemp = tempBase + "_audio.m4a";
                }
                else
                {
                    _caminhoAudioTemp = tempBase + "_audio.wav";
                }

                // 1. Inicia o processo FFmpeg de vídeo usando NVENC acelerado por GPU como preferência
                IniciarVideoProcess(usarNVENC: true);

                // 2. O processo do FFmpeg de áudio será iniciado de forma "lazy" na thread EscreverAudioLoop,
                // garantindo que já tenhamos detectado a taxa de amostragem e canais reais a partir do primeiro frame
                // e gravando em containers estruturados (.m4a ou .wav) com metadados e timestamps de alta precisão.

                // 3. Thread que consome a fila de vídeo e escreve no stdin do FFmpeg
                _videoWriterThread = new Thread(EscreverVideoLoop)
                {
                    IsBackground = true,
                    Name = $"VideoWriter_{NomeFonte}"
                };
                _videoWriterThread.Start();

                // 4. Thread que consome a fila de áudio
                _audioWriterThread = new Thread(EscreverAudioLoop)
                {
                    IsBackground = true,
                    Name = $"AudioWriter_{NomeFonte}"
                };
                _audioWriterThread.Start();

                Console.WriteLine($"[Gravador] Gravação iniciada para '{NomeFonte}' -> {CaminhoArquivo} (Áudio: {_formatoAudio.ToUpper()})");
                Console.WriteLine($"[Gravador]   Dimensões de gravação ajustadas: {_width}x{_height}");
                Console.WriteLine($"[Gravador]   Vídeo temp: {_caminhoVideoTemp}");
                Console.WriteLine($"[Gravador]   Áudio temp: {_caminhoAudioTemp}");
            }
            catch (Exception ex)
            {
                Gravando = false;
                _rodandoThreads = false;
                _stdinVideo?.Close();
                try { if (_procVideo != null && !_procVideo.HasExited) _procVideo.Kill(); } catch { }
                _stdinAudio?.Close();
                try { if (_procAudio != null && !_procAudio.HasExited) _procAudio.Kill(); } catch { }
                throw new InvalidOperationException($"Falha ao inicializar o gravador para '{NomeFonte}': {ex.Message}", ex);
            }
        }
    }

    private void IniciarAudioProcess()
    {
        string codecArg = _formatoAudio == "aac" ? "-c:a aac -b:a 192k" : "-c:a pcm_s16le";
        string audioArgs = $"-hide_banner -y -f s16le -ar {_sampleRate} -ac {_channels} -i pipe:0 {codecArg} \"{_caminhoAudioTemp}\"";

        var procAudioInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = audioArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        try
        {
            _procAudio = new System.Diagnostics.Process { StartInfo = procAudioInfo };
            _procAudio.Start();
            _stdinAudio = _procAudio.StandardInput.BaseStream;

            Task.Run(() =>
            {
                try
                {
                    using var reader = _procAudio.StandardError;
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        // Descomente abaixo se precisar debugar o FFmpeg de áudio:
                        // if (line != null) Console.WriteLine($"[FFmpeg-Audio-Err] {line}");
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Nao foi possivel iniciar o processo FFmpeg de audio: {ex.Message}", ex);
        }
    }

    private void IniciarVideoProcess(bool usarNVENC)
    {
        int qpValue = 23; // Média (padrão)
        if (AppConfig.QualidadeGravacao == "alta") qpValue = 18;
        else if (AppConfig.QualidadeGravacao == "baixa") qpValue = 28;

        string videoArgs;
        if (usarNVENC)
        {
            // Aceleração por GPU (NVENC) com qualidade constante (constqp -qp qpValue)
            videoArgs = $"-hide_banner -y -f rawvideo -pix_fmt bgra -s {_width}x{_height} -r {_frameRateN}/{_frameRateD} -i - -c:v h264_nvenc -preset fast -rc constqp -qp {qpValue} -pix_fmt yuv420p -an \"{_caminhoVideoTemp}\"";
        }
        else
        {
            // Fallback para CPU (libx264 ultrafast, alta compatibilidade e baixo custo de CPU de codificação)
            videoArgs = $"-hide_banner -y -f rawvideo -pix_fmt bgra -s {_width}x{_height} -r {_frameRateN}/{_frameRateD} -i - -c:v libx264 -preset ultrafast -crf {qpValue} -pix_fmt yuv420p -an \"{_caminhoVideoTemp}\"";
        }

        var procVideoInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = videoArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        try
        {
            _procVideo = new System.Diagnostics.Process { StartInfo = procVideoInfo };
            _procVideo.Start();
            _stdinVideo = _procVideo.StandardInput.BaseStream;

            string rotuloLog = usarNVENC ? "NVENC" : "CPU";
            Task.Run(() =>
            {
                try
                {
                    using var r = _procVideo.StandardError;
                    while (!r.EndOfStream)
                    {
                        string? l = r.ReadLine();
                        if (!string.IsNullOrEmpty(l))
                        {
                            // Oculta logs de progresso contínuos do FFmpeg ("frame=", "fps=", etc.) a menos que o diagnóstico esteja ativo
                            bool ehProgresso = l.Contains("frame=") || l.Contains("fps=") || l.Contains("size=");
                            if (AppConfig.HabilitarLogsDiagnostico || !ehProgresso)
                            {
                                Console.WriteLine($"[FFmpeg-Video-{rotuloLog}-{NomeFonte}] {l}");
                            }
                        }
                    }
                }
                catch { }
            });

            // Se for inicialização por NVENC, agenda verificação rápida para fallback se cair imediatamente
            if (usarNVENC)
            {
                var procAgendado = _procVideo;
                Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    lock (_lock)
                    {
                        if (Gravando && _procVideo == procAgendado && _procVideo != null && _procVideo.HasExited && _procVideo.ExitCode != 0)
                        {
                            Console.WriteLine($"[!] FFmpeg com NVENC terminou imediatamente com código {_procVideo.ExitCode}. Iniciando fallback para CPU...");
                            try { _stdinVideo?.Close(); } catch { }
                            IniciarVideoProcess(usarNVENC: false);
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            if (usarNVENC)
            {
                Console.WriteLine($"[!] Falha ao iniciar FFmpeg acelerado por GPU: {ex.Message}. Tentando fallback direto para CPU...");
                IniciarVideoProcess(usarNVENC: false);
            }
            else
            {
                throw new InvalidOperationException($"Falha crítica ao iniciar FFmpeg em modo CPU: {ex.Message}", ex);
            }
        }
    }

    public void EscreverFrame(Mat frame)
    {
        if (!Gravando) return;

        try
        {
            Mat framePronto = frame;
            bool precisaDispose = false;

            // Se as dimensões originais do feed NDI forem ímpares, redimensiona rapidamente usando Nearest Neighbor
            if (frame.Width != _width || frame.Height != _height)
            {
                framePronto = new Mat();
                Cv2.Resize(frame, framePronto, new OpenCvSharp.Size(_width, _height), 0, 0, InterpolationFlags.Nearest);
                precisaDispose = true;
            }

            int totalBytes = _width * _height * 4;
            byte[] buffer = new byte[totalBytes];
            Marshal.Copy(framePronto.Data, buffer, 0, totalBytes);

            if (precisaDispose)
            {
                framePronto.Dispose();
            }

            lock (_frameLock)
            {
                _ultimoFrameBytes = buffer;
            }
        }
        catch { }
    }

    private int _frameCountAudio = 0;

    public unsafe void EscreverAudio(NDIlib.audio_frame_v3_t audioFrame)
    {
        if (!Gravando) return;

        try
        {
            int noChannels = audioFrame.no_channels;
            int noSamples = audioFrame.no_samples;
            int stride = audioFrame.channel_stride_in_bytes;

            if (noChannels <= 0 || noSamples <= 0 || audioFrame.p_data == IntPtr.Zero) return;

            if (AppConfig.HabilitarLogsDiagnostico && _frameCountAudio < 5)
            {
                byte* pSrc = (byte*)audioFrame.p_data.ToPointer();
                string hex = "";
                int maxBytesToPrint = Math.Min(64, noSamples * noChannels * 4);
                for (int i = 0; i < maxBytesToPrint; i++)
                {
                    hex += pSrc[i].ToString("X2") + " ";
                }
                Console.WriteLine($"[Diagnostico-Audio-Cru-{NomeFonte}] Frame #{_frameCountAudio}: rate={audioFrame.sample_rate}, channels={noChannels}, samples={noSamples}, hex={hex}");
            }

            // Detecta e atualiza dinamicamente a taxa de amostragem e canais no primeiro frame recebido
            if (_sampleRate != audioFrame.sample_rate || _channels != noChannels)
            {
                _sampleRate = audioFrame.sample_rate;
                _channels = noChannels;
                Console.WriteLine($"[Gravador-{NomeFonte}] Audio NDI detectado: {_sampleRate} Hz, {_channels} canal(is).");
            }

            lock (_lock)
            {
                if (_tempoInicioGravacao == null)
                {
                    _tempoInicioGravacao = DateTime.Now;
                }

                // CFR de Áudio: preenche lacunas por perda de frames de áudio com silêncio para manter a sincronia perfeita
                double segundosDecorridos = (DateTime.Now - _tempoInicioGravacao.Value).TotalSeconds;
                long amostrasEsperadas = (long)(segundosDecorridos * _sampleRate);
                long lacunaAmostras = amostrasEsperadas - _totalAmostrasGravadas;
                long toleranciaAmostras = (long)(0.100 * _sampleRate); // tolerância de 100ms

                if (lacunaAmostras > toleranciaAmostras)
                {
                    // Preenche a lacuna de áudio com silêncio (PCM 16-bit zeros), independente do tempo do gap,
                    // garantindo alinhamento temporal CFR contínuo com a trilha de vídeo.
                    int bytesSilencio = (int)(lacunaAmostras * _channels * sizeof(short));
                    byte[] silencioBuffer = new byte[bytesSilencio];
                    _audioQueue.Enqueue(silencioBuffer);
                    _totalAmostrasGravadas += lacunaAmostras;
                    Console.WriteLine($"[Gravador-{NomeFonte}] Lacuna de áudio preenchida com silêncio: {lacunaAmostras} amostras (~{(double)lacunaAmostras/_sampleRate:F2}s).");
                }
            }

            int totalBytes = noSamples * noChannels * sizeof(short);
            byte[] byteBuffer = new byte[totalBytes];
            float maxAbs = 0f;

            fixed (byte* pDstBytes = byteBuffer)
            {
                short* pDst = (short*)pDstBytes;
                byte* pSrcBase = (byte*)audioFrame.p_data.ToPointer();

                for (int s = 0; s < noSamples; s++)
                {
                    for (int c = 0; c < noChannels; c++)
                    {
                        float* pSrcChannel = (float*)(pSrcBase + c * stride);
                        float val = pSrcChannel[s];
                        
                        // Limita o valor de float entre -1.0 e 1.0 para evitar clipping
                        if (val > 1.0f) val = 1.0f;
                        else if (val < -1.0f) val = -1.0f;

                        // Converte para PCM 16-bit (short)
                        pDst[s * noChannels + c] = (short)(val * 32767f);
                        
                        float absVal = Math.Abs(val);
                        if (absVal > maxAbs) maxAbs = absVal;
                    }
                }
            }

            if (AppConfig.HabilitarLogsDiagnostico && _frameCountAudio % 100 == 0)
            {
                Console.WriteLine($"[Diagnostico-Audio-{NomeFonte}] Frame #{_frameCountAudio}: {noSamples} amostras, amplitude max = {maxAbs:F6}");
            }
            _frameCountAudio++;

            _audioQueue.Enqueue(byteBuffer);
            _totalAmostrasGravadas += noSamples;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Erro no processamento de áudio NDI para '{NomeFonte}': {ex.Message}");
        }
    }

    private void EscreverVideoLoop()
    {
        long totalFramesGravados = 0;
        DateTime? ultimoPrintDebug = null;
        double fps = _frameRateN / (double)_frameRateD;
        
        // Aguarda pelo menos o primeiro frame chegar antes de iniciar o loop de tempo
        while (_rodandoThreads && Gravando && _ultimoFrameBytes == null)
        {
            Thread.Sleep(10);
        }

        lock (_lock)
        {
            if (_tempoInicioGravacao == null)
            {
                _tempoInicioGravacao = DateTime.Now;
            }
        }

        while (_rodandoThreads && Gravando)
        {
            // Verifica se o processo FFmpeg ainda está rodando. Se caiu, fazemos a checagem se há transição para CPU.
            bool processoFalhou = false;
            lock (_lock)
            {
                if (_procVideo != null && _procVideo.HasExited && _procVideo.ExitCode != 0)
                {
                    processoFalhou = true;
                }
            }

            if (processoFalhou)
            {
                // Espera curto intervalo para a thread de fallback iniciar o processo em CPU
                Thread.Sleep(200);
                lock (_lock)
                {
                    // Se o processo ainda é o que falhou, encerra
                    if (_procVideo != null && _procVideo.HasExited)
                    {
                        Console.WriteLine($"[!] FFmpeg de vídeo terminou de forma inesperada. Encerrando loop de escrita.");
                        PararInternal();
                        return;
                    }
                }
            }

            byte[]? buffer = null;
            lock (_frameLock)
            {
                buffer = _ultimoFrameBytes;
            }

            if (buffer != null)
            {
                Stream? currentStdin = _stdinVideo;
                if (currentStdin != null && Gravando)
                {
                    try
                    {
                        double segundosDecorridos;
                        lock (_lock)
                        {
                            segundosDecorridos = (DateTime.Now - _tempoInicioGravacao.Value).TotalSeconds;
                        }

                        long framesEsperados = (long)(segundosDecorridos * fps);
                        long framesFaltando = framesEsperados - totalFramesGravados;

                        // Se estiver atrasado, envia múltiplos frames para compensar
                        if (framesFaltando > 0)
                        {
                            // Limita o multiplicador para evitar picos excessivos caso o sistema dê uma travada longa
                            long framesParaGravar = Math.Min(framesFaltando, 15);
                            for (int i = 0; i < framesParaGravar; i++)
                            {
                                currentStdin.Write(buffer, 0, buffer.Length);
                                totalFramesGravados++;
                            }
                            currentStdin.Flush();

                            if (totalFramesGravados == 1 || (totalFramesGravados - framesParaGravar == 0))
                            {
                                Console.WriteLine($"[Gravador] Primeiro frame enviado com sucesso para '{NomeFonte}' (resolução: {_width}x{_height} a {fps:F2} FPS).");
                            }
                        }

                        // Print de diagnóstico de sincronia no console a cada 3 segundos
                        if (AppConfig.HabilitarLogsDiagnostico && (ultimoPrintDebug == null || (DateTime.Now - ultimoPrintDebug.Value).TotalSeconds >= 3.0))
                        {
                            ultimoPrintDebug = DateTime.Now;
                            double segVideo = totalFramesGravados / fps;
                            double segAudio = (double)_totalAmostrasGravadas / _sampleRate;
                            double desvioMs = (segAudio - segVideo) * 1000.0;
                            
                            Console.WriteLine($"[DIAGNOSTICO-SINCRONIA-{NomeFonte}] Decorrido: {segundosDecorridos:F1}s | Video: {totalFramesGravados} frames ({segVideo:F2}s) | Audio: {_totalAmostrasGravadas} amostras ({segAudio:F2}s) | Desvio: {desvioMs:F1} ms | FilaAudio: {_audioQueue.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        bool emFallback = false;
                        lock (_lock)
                        {
                            if (_procVideo != null && _procVideo.HasExited && _procVideo.ExitCode != 0)
                            {
                                emFallback = true;
                            }
                        }

                        if (emFallback)
                        {
                            // Aguarda e tenta novamente com o novo pipe do CPU no próximo ciclo
                            Thread.Sleep(100);
                            continue;
                        }

                        Console.WriteLine($"[!] Erro ao escrever frame no stdin: {ex.Message}");
                        PararInternal();
                        return;
                    }
                }
            }

            // Sleep curto para reagir rápido a picos de escalonamento do processador
            Thread.Sleep(5);
        }
        
        double tempoFinal;
        lock (_lock)
        {
            tempoFinal = _tempoInicioGravacao != null ? (DateTime.Now - _tempoInicioGravacao.Value).TotalSeconds : 0;
        }
        Console.WriteLine($"[Gravador] Loop de vídeo encerrado para '{NomeFonte}'. Total de frames enviados: {totalFramesGravados} ({tempoFinal:F2}s)");
    }

    private void EscreverAudioLoop()
    {
        while (_rodandoThreads && Gravando)
        {
            bool escreveu = false;
            while (_audioQueue.TryDequeue(out byte[]? audioBytes))
            {
                try
                {
                    if (_procAudio == null)
                    {
                        IniciarAudioProcess();
                    }
                    _stdinAudio?.Write(audioBytes, 0, audioBytes.Length);
                    escreveu = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao gravar bloco de áudio: {ex.Message}");
                }
            }

            if (!escreveu)
                Thread.Sleep(5);
        }

        // Garante que todo o áudio restante na fila seja escrito antes de fechar a thread
        while (_audioQueue.TryDequeue(out byte[]? audioBytes))
        {
            try
            {
                if (_procAudio == null)
                {
                    IniciarAudioProcess();
                }
                _stdinAudio?.Write(audioBytes, 0, audioBytes.Length);
            }
            catch { }
        }

        try { _stdinAudio?.Flush(); } catch { }
    }

    public void Parar()
    {
        lock (_lock)
        {
            PararInternal();
        }
    }

    private void PararInternal()
    {
        if (!Gravando) return;
        Gravando = false;

        // Preenche qualquer lacuna final de áudio até o instante de encerramento do vídeo
        if (_tempoInicioGravacao != null && _totalAmostrasGravadas > 0)
        {
            double segundosDecorridos = (DateTime.Now - _tempoInicioGravacao.Value).TotalSeconds;
            long amostrasEsperadas = (long)(segundosDecorridos * _sampleRate);
            long lacunaAmostras = amostrasEsperadas - _totalAmostrasGravadas;

            // Só preenche se a lacuna for positiva e menor que 5 segundos (evita preenchimento excessivo)
            if (lacunaAmostras > 0 && lacunaAmostras < _sampleRate * 5)
            {
                int bytesSilencio = (int)(lacunaAmostras * _channels * sizeof(short));
                byte[] silencioBuffer = new byte[bytesSilencio];
                _audioQueue.Enqueue(silencioBuffer);
                _totalAmostrasGravadas += lacunaAmostras;
                Console.WriteLine($"[Gravador-{NomeFonte}] Lacuna de áudio final preenchida com silêncio: {lacunaAmostras} amostras (~{(double)lacunaAmostras/_sampleRate:F2}s).");
            }
        }

        _rodandoThreads = false;

        // Encerra threads de gravação
        try { _videoWriterThread?.Join(1000); } catch { }
        try { _audioWriterThread?.Join(1000); } catch { }

        // Fecha e encerra o processo de vídeo
        try { _stdinVideo?.Close(); } catch { }
        try
        {
            if (_procVideo != null && !_procVideo.HasExited)
            {
                _procVideo.WaitForExit(4000);
                if (!_procVideo.HasExited) _procVideo.Kill();
            }
        }
        catch { }
        finally
        {
            _stdinVideo = null;
            _procVideo?.Dispose();
            _procVideo = null;
        }

        // Fecha e encerra o processo de áudio (se for aac)
        try { _stdinAudio?.Close(); } catch { }
        try
        {
            if (_procAudio != null && !_procAudio.HasExited)
            {
                _procAudio.WaitForExit(4000);
                if (!_procAudio.HasExited) _procAudio.Kill();
            }
        }
        catch { }
        finally
        {
            _stdinAudio = null;
            _procAudio?.Dispose();
            _procAudio = null;
        }

        Console.WriteLine($"[Gravador] Arquivos temporários fechados para '{NomeFonte}'. Iniciando pós-processamento (muxing)...");

        string videoTemp = _caminhoVideoTemp ?? "";
        string audioTemp = _caminhoAudioTemp ?? "";
        string saida = CaminhoArquivo;

        double duracaoGravacao = 10.0;
        if (_tempoInicioGravacao.HasValue)
        {
            duracaoGravacao = (DateTime.Now - _tempoInicioGravacao.Value).TotalSeconds;
        }
        if (duracaoGravacao < 1.0) duracaoGravacao = 1.0;

        var statusMux = new MuxingStatus
        {
            NomeFonte = NomeFonte,
            Progresso = 0,
            Concluido = false
        };

        lock (AppConfig.LockMuxing)
        {
            AppConfig.ProcessosMuxing[NomeFonte] = statusMux;
        }
        SseManager.NotificarClientes();

        Task.Run(() =>
        {
            System.Diagnostics.Process? muxProc = null;
            try
            {
                bool temAudio = File.Exists(audioTemp) && new FileInfo(audioTemp).Length > 0;
                string muxArgs;

                if (temAudio)
                {
                    if (_formatoAudio == "aac")
                    {
                        // Cópia ultra-rápida de vídeo e áudio já comprimidos (m4a -> mp4)
                        muxArgs = $"-hide_banner -y -i \"{videoTemp}\" -i \"{audioTemp}\" -c:v copy -c:a copy -map 0:v -map 1:a \"{saida}\"";
                    }
                    else
                    {
                        // Codifica o áudio sem perdas WAV para AAC durante o muxing (wav -> mp4)
                        // O container WAV já possui a amostragem e canais definidos de forma estruturada em seu cabeçalho
                        muxArgs = $"-hide_banner -y -i \"{videoTemp}\" -i \"{audioTemp}\" -c:v copy -c:a aac -b:a 192k -map 0:v -map 1:a \"{saida}\"";
                    }
                }
                else
                {
                    muxArgs = $"-hide_banner -y -i \"{videoTemp}\" -c:v copy \"{saida}\"";
                }

                var muxInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = muxArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                Console.WriteLine($"[Gravador] Comando de Muxing: ffmpeg {muxArgs}");

                muxProc = new System.Diagnostics.Process { StartInfo = muxInfo };
                muxProc.Start();

                var timeRegex = new System.Text.RegularExpressions.Regex(@"time=\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                DateTime ultimaNotificacao = DateTime.MinValue;

                Task.Run(() =>
                {
                    try
                    {
                        using var r = muxProc.StandardError;
                        while (!r.EndOfStream)
                        {
                            string? l = r.ReadLine();
                            if (!string.IsNullOrEmpty(l))
                            {
                                if (AppConfig.HabilitarLogsDiagnostico)
                                    Console.WriteLine($"[FFmpeg-Mux] {l}");

                                var match = timeRegex.Match(l);
                                if (match.Success)
                                {
                                    int hrs = int.Parse(match.Groups[1].Value);
                                    int mins = int.Parse(match.Groups[2].Value);
                                    int secs = int.Parse(match.Groups[3].Value);
                                    int cents = int.Parse(match.Groups[4].Value);

                                    double processado = hrs * 3600 + mins * 60 + secs + cents / 100.0;
                                    double pct = (processado / duracaoGravacao) * 100.0;
                                    if (pct > 99.0) pct = 99.0;

                                    statusMux.Progresso = pct;

                                    if ((DateTime.Now - ultimaNotificacao).TotalMilliseconds >= 300)
                                    {
                                        ultimaNotificacao = DateTime.Now;
                                        SseManager.NotificarClientes();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                });

                muxProc.WaitForExit(30000);

                if (muxProc.ExitCode == 0)
                {
                    Console.WriteLine($"[Gravador] Sucesso total! Gravação salva em: {saida}");
                    statusMux.Progresso = 100.0;
                    statusMux.Concluido = true;
                    SseManager.NotificarClientes();

                    Task.Delay(3000).ContinueWith(_ =>
                    {
                        lock (AppConfig.LockMuxing)
                        {
                            AppConfig.ProcessosMuxing.Remove(NomeFonte);
                        }
                        SseManager.NotificarClientes();
                    });

                    if (AppConfig.ApagarTemporarios)
                    {
                        try
                        {
                            if (File.Exists(videoTemp)) File.Delete(videoTemp);
                            if (File.Exists(audioTemp)) File.Delete(audioTemp);
                            Console.WriteLine($"[Gravador] Arquivos temporários deletados com sucesso.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Erro ao deletar arquivos temporários: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"[!] Muxing falhou com código {muxProc.ExitCode}. O arquivo temporário de vídeo foi preservado em: {videoTemp}");
                    statusMux.Erro = $"Falha no muxing ({muxProc.ExitCode})";
                    SseManager.NotificarClientes();

                    Task.Delay(6000).ContinueWith(_ =>
                    {
                        lock (AppConfig.LockMuxing)
                        {
                            AppConfig.ProcessosMuxing.Remove(NomeFonte);
                        }
                        SseManager.NotificarClientes();
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Erro no muxing: {ex.Message}");
                statusMux.Erro = ex.Message;
                SseManager.NotificarClientes();

                Task.Delay(6000).ContinueWith(_ =>
                {
                    lock (AppConfig.LockMuxing)
                    {
                        AppConfig.ProcessosMuxing.Remove(NomeFonte);
                    }
                    SseManager.NotificarClientes();
                });
            }
            finally
            {
                if (AppConfig.ApagarTemporarios && muxProc != null && muxProc.ExitCode == 0)
                {
                    Console.WriteLine($"[Gravador] Processo concluído. Arquivos temporários limpos.");
                }
                else
                {
                    Console.WriteLine($"[Gravador] Temporários preservados - Vídeo: {videoTemp} | Áudio: {audioTemp}");
                }
            }
        });

        Console.WriteLine($"[Gravador] Gravação parada para '{NomeFonte}'. Muxing rodando em background...");
    }
}

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
                            if (AppConfig.ReceptoresAtivos.TryGetValue(nomeAtivo, out var rec))
                            {
                                AppConfig.ReceptoresAtivos.Remove(nomeAtivo);
                                Task.Run(() => rec.Parar());
                            }

                            // Para a gravação associada a este feed, se estiver ativa
                            GravadorFFmpeg? gravadorParaParar = null;
                            lock (AppConfig.LockGravadores)
                            {
                                if (AppConfig.GravadoresAtivos.TryGetValue(nomeAtivo, out var g))
                                {
                                    gravadorParaParar = g;
                                    AppConfig.GravadoresAtivos.Remove(nomeAtivo);
                                }
                            }
                            if (gravadorParaParar != null)
                            {
                                Task.Run(() => gravadorParaParar.Parar());
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
                        }
                    }

                    SseManager.NotificarClientes();
                }
            }

            Thread.Sleep(2000);
        }

        NDIlib.find_destroy(pFind);
    }
}

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
}

// ===========================================================================
// MOTOR DE VÍDEO (RUST-LIKE PERFORMANCE EM LERP E COMPOSIÇÃO)
// ===========================================================================
public static class VideoEngine
{
    private static Thread? _engineThread;
    private static bool _running = false;
    private static readonly Dictionary<string, PosicaoFeed> _posicoesAtuais = new();
    private const float LERP_FATOR = 0.50f;

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

        var videoFrame = new NDIlib.video_frame_v2_t
        {
            xres = 1920,
            yres = 850,
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            line_stride_in_bytes = 1920 * 4,
            frame_rate_N = 30000,
            frame_rate_D = 1001,
            picture_aspect_ratio = 1920f / 850f,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive
        };

        var videoFrameV = new NDIlib.video_frame_v2_t
        {
            xres = 550,
            yres = 850,
            FourCC = NDIlib.FourCC_type_e.FourCC_type_BGRA,
            line_stride_in_bytes = 550 * 4,
            frame_rate_N = 30000,
            frame_rate_D = 1001,
            picture_aspect_ratio = 550f / 850f,
            frame_format_type = NDIlib.frame_format_type_e.frame_format_type_progressive
        };

        const int W = 1920;
        const int H = 850;

        const int WV = 550;
        const int HV = 850;

        // Inicializa os canvas estáticos persistentes
        _canvasPrincipal = new Mat(H, W, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));
        _canvasVertical = new Mat(HV, WV, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));

        IntPtr pAudioBufferNativo = Marshal.AllocHGlobal(AudioMixer.TamanhoBloco * AudioMixer.CanaisSaida * sizeof(float));

        while (_running)
        {
            var startTime = DateTime.Now;
            int pad = AppConfig.PaddingMosaico;

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
                var alvos = CalcularPosicoesAlvo(framesAtivos, AppConfig.FonteHighlight, AppConfig.FonteSolo, W, H, pad);

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

            // -------------------------------------------------------------
            // Renderiza Canvas Vertical
            // -------------------------------------------------------------
            var canvasV = _canvasVertical;
            canvasV.SetTo(bgScalar);

            if (framesAtivos.Count == 0)
            {
                Cv2.PutText(canvasV, "Aguardando...", new OpenCvSharp.Point(60, HV / 2),
                    HersheyFonts.HersheySimplex, 1.0, new Scalar(100, 100, 100, 255), 2, LineTypes.AntiAlias);
            }
            else
            {
                int padV = 8;
                int nVis = Math.Min(framesAtivos.Count, 4);
                int hBloco = (HV - (nVis + 1) * padV) / nVis;

                for (int i = 0; i < nVis; i++)
                {
                    var feed = framesAtivos[i];
                    int py = padV + i * (hBloco + padV);

                    int fontSize = 32;
                    int mw = WV - 2 * padV;
                    if (mw < 600) fontSize = 24;

                    var fontGC = ObterFonteAnton(fontSize);
                    DesenharComAspectRatio(canvasV, feed.Frame, padV, py, mw, hBloco, feed.Apelido, fontGC);
                }
            }

            videoFrameV.p_data = canvasV.Data;
            videoFrameV.timecode = timecodeComum;
            NDIlib.send_send_video_v2(pNdiSendV, ref videoFrameV);

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

public class PosicaoFeed
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    public PosicaoFeed(float x, float y, float w, float h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }
}

// ===========================================================================
// ESCRITOR DE CONSOLE PERSONALIZADO COM TIMESTAMP
// ===========================================================================
public class TimePrefixedTextWriter : TextWriter
{
    private readonly TextWriter _originalOut;
    private bool _needsPrefix = true;

    public TimePrefixedTextWriter(TextWriter originalOut)
    {
        _originalOut = originalOut;
    }

    public override System.Text.Encoding Encoding => _originalOut.Encoding;

    public override void Write(char value)
    {
        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(value);
        if (value == '\n')
        {
            _needsPrefix = true;
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(value);
        if (value.EndsWith("\n") || value.EndsWith("\r"))
        {
            _needsPrefix = true;
        }
    }

    public override void WriteLine(string? value)
    {
        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
        }
        _originalOut.WriteLine(value);
        _needsPrefix = true;
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        if (buffer == null || count == 0) return;
        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(buffer, index, count);
        if (buffer[index + count - 1] == '\n' || buffer[index + count - 1] == '\r')
        {
            _needsPrefix = true;
        }
    }

    public override void WriteLine()
    {
        _originalOut.WriteLine();
        _needsPrefix = true;
    }
}

// ===========================================================================
// CLASSE E PONTO DE ENTRADA PRINCIPAL (WEB HOST + MOTOR)
// ===========================================================================
class Program
{
    static void Main(string[] args)
    {
        Console.SetOut(new TimePrefixedTextWriter(Console.Out));

        if (!NDIlib.initialize())
        {
            Console.WriteLine("[!] Erro crítico: Falha ao inicializar a NDI SDK.");
            return;
        }

        NdiScanner.Iniciar();
        VideoEngine.Iniciar();
        AppConfig.MixerGlobal.Iniciar();
        SseManager.IniciarEnvioVu();

        var builder = WebApplication.CreateBuilder(args);
        
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(8634);
        });

        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            });
        });

        var app = builder.Build();
        app.UseCors();

        // -------------------------------------------------------------
        // ROTAS WEB E RECURSOS ESTÁTICOS
        // -------------------------------------------------------------
        
        // Servir arquivos CSS estáticos
        app.MapGet("/static/css/comum.css", async (HttpContext context) =>
        {
            var caminho = ObterCaminhoFisico(Path.Combine("web", "static", "css", "comum.css"));
            if (caminho == null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = "text/css; charset=utf-8";
            await context.Response.SendFileAsync(caminho);
        });

        // Servir arquivos JS estáticos
        app.MapGet("/static/js/comum.js", async (HttpContext context) =>
        {
            var caminho = ObterCaminhoFisico(Path.Combine("web", "static", "js", "comum.js"));
            if (caminho == null)
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = "application/javascript; charset=utf-8";
            await context.Response.SendFileAsync(caminho);
        });

        // Página Inicial: Serve painel.html
        app.MapGet("/", async (HttpContext context) =>
        {
            var caminho = ObterCaminhoFisico(Path.Combine("web", "templates", "painel.html"));
            if (caminho != null)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(caminho);
            }
            else
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Erro: painel.html nao encontrado.");
            }
        });

        // Rota do OBS Dock: Serve dock.html compactado
        app.MapGet("/dock", async (HttpContext context) =>
        {
            var caminho = ObterCaminhoFisico(Path.Combine("web", "templates", "dock.html"));
            if (caminho != null)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(caminho);
            }
            else
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("Erro: dock.html nao encontrado.");
            }
        });

        // API: Listar Fontes
        app.MapGet("/api/fontes", () =>
        {
            lock (AppConfig.LockFontes)
            {
                var dados = new List<object>();
                foreach (var n in AppConfig.FontesNaRede)
                {
                    bool ativo = Array.IndexOf(AppConfig.OrdemReceptores, n) != -1;
                    int posicao = ativo ? Array.IndexOf(AppConfig.OrdemReceptores, n) : -1;
                    string apelido = AppConfig.ApelidosFontes.TryGetValue(n, out var ap) ? ap : "";
                    bool erro = false;
                    string resolucaoVal = "";
                    double fpsVal = 0.0;
                    
                    if (AppConfig.ReceptoresAtivos.TryGetValue(n, out var rec))
                    {
                        erro = rec.Erro;
                        if (rec.XRes > 0 && rec.YRes > 0)
                        {
                            resolucaoVal = $"{rec.XRes}x{rec.YRes}";
                            fpsVal = rec.Fps;
                        }
                    }

                    bool gravando = false;
                    long? gravandoDesde = null;
                    lock (AppConfig.LockGravadores)
                    {
                        if (AppConfig.GravadoresAtivos.TryGetValue(n, out var gravador))
                        {
                            gravando = true;
                            if (gravador.TempoInicioGravacao.HasValue)
                            {
                                gravandoDesde = new DateTimeOffset(gravador.TempoInicioGravacao.Value.ToUniversalTime()).ToUnixTimeSeconds();
                            }
                            else
                            {
                                gravandoDesde = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            }
                        }
                    }

                    object? muxingObj = null;
                    lock (AppConfig.LockMuxing)
                    {
                        if (AppConfig.ProcessosMuxing.TryGetValue(n, out var status))
                        {
                            muxingObj = new
                            {
                                progresso = Math.Round(status.Progresso, 1),
                                concluido = status.Concluido,
                                erro = status.Erro
                            };
                        }
                    }

                    int volAtual = 100;
                    lock (AppConfig.LockVolumes)
                    {
                        if (AppConfig.VolumesFontes.TryGetValue(n, out float v))
                        {
                            volAtual = (int)Math.Round(v * 100);
                        }
                    }

                    dados.Add(new
                    {
                        nome = n,
                        ativo = ativo,
                        highlight = (n == AppConfig.FonteHighlight),
                        solo = (n == AppConfig.FonteSolo),
                        posicao = posicao,
                        apelido = apelido,
                        erro = erro,
                        resolucao = resolucaoVal,
                        fps = fpsVal,
                        gravando = gravando,
                        gravando_desde = gravandoDesde,
                        muxing = muxingObj,
                        volume = volAtual
                    });
                }
                return Results.Json(dados);
            }
        });

        // API: Alternar ativação de um feed na cena
        app.MapPost("/toggle/{*nome}", (string nome) =>
        {
            ReceptorNDI? recParaParar = null;

            lock (AppConfig.LockFontes)
            {
                // Verifica se a fonte já está adicionada na cena (em OrdemReceptores)
                int indexCena = Array.IndexOf(AppConfig.OrdemReceptores, nome);
                bool estaNaCena = indexCena != -1;

                if (estaNaCena)
                {
                    // REMOVER DA CENA
                    AppConfig.OrdemReceptores[indexCena] = null;

                    if (AppConfig.FonteHighlight == nome) AppConfig.FonteHighlight = null;
                    if (AppConfig.FonteSolo == nome) AppConfig.FonteSolo = null;

                    // Verifica se a fonte está sendo gravada
                    bool estaGravando = false;
                    lock (AppConfig.LockGravadores)
                    {
                        estaGravando = AppConfig.GravadoresAtivos.ContainsKey(nome);
                    }

                    if (!estaGravando)
                    {
                        // Se não estiver gravando, podemos desconectar e parar o ReceptorNDI da rede
                        if (AppConfig.ReceptoresAtivos.TryGetValue(nome, out var rec))
                        {
                            recParaParar = rec;
                            AppConfig.ReceptoresAtivos.Remove(nome);
                        }
                        Console.WriteLine($"[-] Desconectado e removido da cena: {nome}");
                    }
                    else
                    {
                        // Se estiver gravando, mantemos o ReceptorNDI ativo em segundo plano!
                        Console.WriteLine($"[-] Removido da cena mas mantido em background para gravacao: {nome}");
                    }
                }
                else
                {
                    // ADICIONAR À CENA
                    // O limite de 4 é estritamente para o número de participantes visíveis no mosaico da cena
                    int countCena = AppConfig.OrdemReceptores.Count(n => !string.IsNullOrEmpty(n));
                    if (countCena >= 4)
                    {
                        Console.WriteLine($"[!] Limite de 4 feeds visiveis na cena atingido. Nao foi possivel adicionar: {nome}");
                        return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos na cena atingido." });
                    }

                    // Se já existir um receptor ativo (porque estava gravando em background), apenas usamos ele
                    if (!AppConfig.ReceptoresAtivos.ContainsKey(nome))
                    {
                        AppConfig.ReceptoresAtivos[nome] = new ReceptorNDI(nome);
                        Console.WriteLine($"[+] Conectando e adicionando a cena: {nome}");
                    }
                    else
                    {
                        Console.WriteLine($"[+] Trazendo feed que ja estava gravando em background para a cena: {nome}");
                    }

                    lock (AppConfig.LockVolumes)
                    {
                        if (!AppConfig.VolumesFontes.ContainsKey(nome))
                        {
                            AppConfig.VolumesFontes[nome] = 1.0f;
                        }
                    }

                    // Encontra um slot livre no mosaico
                    for (int i = 0; i < 4; i++)
                    {
                        if (string.IsNullOrEmpty(AppConfig.OrdemReceptores[i]))
                        {
                            AppConfig.OrdemReceptores[i] = nome;
                            break;
                        }
                    }
                }
            }

            if (recParaParar != null)
            {
                Task.Run(() => recParaParar.Parar());
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // Helper no C# para garantir que uma fonte esteja ativa na cena
        bool GarantirFonteAtiva(string nome)
        {
            lock (AppConfig.LockFontes)
            {
                if (AppConfig.ReceptoresAtivos.ContainsKey(nome))
                {
                    // Se já estiver ativa mas não na cena, adiciona na cena
                    if (Array.IndexOf(AppConfig.OrdemReceptores, nome) == -1)
                    {
                        int countCena = AppConfig.OrdemReceptores.Count(n => !string.IsNullOrEmpty(n));
                        if (countCena >= 4) return false;

                        for (int i = 0; i < 4; i++)
                        {
                            if (string.IsNullOrEmpty(AppConfig.OrdemReceptores[i]))
                            {
                                AppConfig.OrdemReceptores[i] = nome;
                                break;
                            }
                        }
                    }
                    return true;
                }

                int countCenaAtiva = AppConfig.OrdemReceptores.Count(n => !string.IsNullOrEmpty(n));
                if (countCenaAtiva >= 4)
                {
                    return false;
                }

                try
                {
                    AppConfig.ReceptoresAtivos[nome] = new ReceptorNDI(nome);
                    for (int i = 0; i < 4; i++)
                    {
                        if (string.IsNullOrEmpty(AppConfig.OrdemReceptores[i]))
                        {
                            AppConfig.OrdemReceptores[i] = nome;
                            break;
                        }
                    }
                    Console.WriteLine($"[+] Conectando automaticamente (via ação): {nome}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Falha ao conectar automaticamente: {ex.Message}");
                    return false;
                }
            }
        }

        // Helper para conectar o receptor na rede em background apenas (para gravação ou preview)
        bool GarantirReceptorConectado(string nome)
        {
            lock (AppConfig.LockFontes)
            {
                if (AppConfig.ReceptoresAtivos.ContainsKey(nome))
                {
                    return true;
                }

                try
                {
                    AppConfig.ReceptoresAtivos[nome] = new ReceptorNDI(nome);
                    Console.WriteLine($"[+] Conectando em background (via gravacao): {nome}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Falha ao conectar em background: {ex.Message}");
                    return false;
                }
            }
        }

        // Funções locais reutilizáveis para Gravação de Feeds
        IResult IniciarGravar(string nome)
        {
            if (string.IsNullOrEmpty(nome))
            {
                return Results.BadRequest(new { status = "error", message = "O nome da fonte nao foi especificado." });
            }

            // Limpa o nome do participante para criar o arquivo com segurança
            string nomeSafe = string.Concat(nome.Split(Path.GetInvalidFileNameChars()));
            nomeSafe = nomeSafe.Replace(" ", "_").Replace("(", "").Replace(")", "");

            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            string nomeArquivo = $"Gravacao_NDI_{nomeSafe}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            string caminhoArquivo = Path.Combine(downloadsPath, nomeArquivo);

            if (!GarantirReceptorConectado(nome))
            {
                return Results.BadRequest(new { status = "error", message = "Nao foi possivel conectar ao feed NDI na rede local." });
            }

            lock (AppConfig.LockGravadores)
            {
                if (AppConfig.GravadoresAtivos.ContainsKey(nome))
                {
                    return Results.Json(new { status = "already_recording", message = "Este participante ja esta sendo gravado.", arquivo = AppConfig.GravadoresAtivos[nome].CaminhoArquivo });
                }

                try
                {
                    var gravador = new GravadorFFmpeg(nome, caminhoArquivo, AppConfig.FormatoAudioAtual);
                    AppConfig.GravadoresAtivos[nome] = gravador;
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { status = "error", message = $"Falha ao configurar gravador: {ex.Message}" });
                }
            }

            SseManager.NotificarClientes();
            Console.WriteLine($"[+] Solicitada gravacao de '{nome}' -> {caminhoArquivo}");
            return Results.Json(new { status = "ok", arquivo = caminhoArquivo });
        }

        IResult PararGravar(string nome)
        {
            if (string.IsNullOrEmpty(nome))
            {
                return Results.BadRequest(new { status = "error", message = "O nome da fonte nao foi especificado." });
            }

            GravadorFFmpeg? gravador = null;

            lock (AppConfig.LockGravadores)
            {
                if (AppConfig.GravadoresAtivos.TryGetValue(nome, out var g))
                {
                    gravador = g;
                    AppConfig.GravadoresAtivos.Remove(nome);
                }
            }

            if (gravador != null)
            {
                Task.Run(() => gravador.Parar());

                ReceptorNDI? recParaParar = null;
                lock (AppConfig.LockFontes)
                {
                    bool estaNaCena = Array.IndexOf(AppConfig.OrdemReceptores, nome) != -1;
                    if (!estaNaCena)
                    {
                        if (AppConfig.ReceptoresAtivos.TryGetValue(nome, out var rec))
                        {
                            recParaParar = rec;
                            AppConfig.ReceptoresAtivos.Remove(nome);
                        }
                    }
                }

                if (recParaParar != null)
                {
                    Task.Run(() => recParaParar.Parar());
                }

                SseManager.NotificarClientes();
                return Results.Json(new { status = "ok" });
            }

            return Results.BadRequest(new { status = "error", message = "Nao ha gravacao ativa para este participante." });
        }

        // API: Iniciar Gravação Individual via FFmpeg acelerado por NVIDIA GPU (NVENC)
        app.MapPost("/api/gravar/iniciar", (string nome) => IniciarGravar(nome));
        app.MapPost("/api/gravar/iniciar/{*nome}", (string nome) => IniciarGravar(nome));

        // API: Parar Gravação Individual
        app.MapPost("/api/gravar/parar", (string nome) => PararGravar(nome));
        app.MapPost("/api/gravar/parar/{*nome}", (string nome) => PararGravar(nome));

        // API: Alternar modo Destaque (Highlight)
        app.MapPost("/api/highlight/{*nome}", (string nome) =>
        {
            if (!GarantirFonteAtiva(nome))
            {
                return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos atingido." });
            }

            lock (AppConfig.LockFontes)
            {
                if (AppConfig.FonteHighlight == nome)
                {
                    AppConfig.FonteHighlight = null;
                    Console.WriteLine($"[*] Highlight desativado para: {nome}");
                }
                else
                {
                    AppConfig.FonteHighlight = nome;
                    Console.WriteLine($"[*] Highlight ativado para: {nome}");
                }
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // API: Alternar modo Solo
        app.MapPost("/api/solo/{*nome}", (string nome) =>
        {
            if (!GarantirFonteAtiva(nome))
            {
                return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos atingido." });
            }

            lock (AppConfig.LockFontes)
            {
                if (AppConfig.FonteSolo == nome)
                {
                    AppConfig.FonteSolo = null;
                    Console.WriteLine($"[*] Solo desativado para: {nome}");
                }
                else
                {
                    AppConfig.FonteSolo = nome;
                    AppConfig.FonteHighlight = null; // Solo cancela highlight
                    Console.WriteLine($"[*] Solo ativado para: {nome}");
                }
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // API: Definir posição no mosaico
        app.MapPost("/api/posicao/{nome}/{novaPos}", (string nome, int novaPos) =>
        {
            if (novaPos < 0 || novaPos > 3)
            {
                return Results.BadRequest(new { status = "error", message = "Posicao invalida (0-3)." });
            }

            bool jaAtiva = false;
            lock (AppConfig.LockFontes)
            {
                jaAtiva = AppConfig.ReceptoresAtivos.ContainsKey(nome);
            }

            if (!jaAtiva)
            {
                lock (AppConfig.LockFontes)
                {
                    if (AppConfig.ReceptoresAtivos.Count >= 4)
                    {
                        return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos atingido." });
                    }
                    
                    try
                    {
                        AppConfig.ReceptoresAtivos[nome] = new ReceptorNDI(nome);
                        
                        var antigoDonoSlot = AppConfig.OrdemReceptores[novaPos];
                        if (antigoDonoSlot != null)
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                if (string.IsNullOrEmpty(AppConfig.OrdemReceptores[i]) && i != novaPos)
                                {
                                    AppConfig.OrdemReceptores[i] = antigoDonoSlot;
                                    break;
                                }
                            }
                        }
                        AppConfig.OrdemReceptores[novaPos] = nome;
                        Console.WriteLine($"[+] Conectando e definindo posicao {novaPos}: {nome}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Falha ao conectar automaticamente: {ex.Message}");
                        return Results.BadRequest(new { status = "error", message = ex.Message });
                    }
                }
            }
            else
            {
                lock (AppConfig.LockFontes)
                {
                    int idxAtual = Array.IndexOf(AppConfig.OrdemReceptores, nome);
                    if (idxAtual == novaPos)
                    {
                        return Results.Json(new { status = "ok" });
                    }

                    if (idxAtual != -1)
                    {
                        var temp = AppConfig.OrdemReceptores[novaPos];
                        AppConfig.OrdemReceptores[novaPos] = AppConfig.OrdemReceptores[idxAtual];
                        AppConfig.OrdemReceptores[idxAtual] = temp;
                        Console.WriteLine($"[#] Troca de posicao: {nome} (de {idxAtual} para {novaPos})");
                    }
                    else
                    {
                        AppConfig.OrdemReceptores[novaPos] = nome;
                    }
                }
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // API: Salvar apelido de GC
        app.MapPost("/api/definir_apelido/{*nome}", async (string nome, HttpContext context) =>
        {
            try
            {
                var dados = await context.Request.ReadFromJsonAsync<Dictionary<string, string>>();
                string apelido = "";
                if (dados != null && dados.TryGetValue("apelido", out var ap))
                {
                    apelido = ap.Trim();
                }

                lock (AppConfig.LockFontes)
                {
                    AppConfig.ApelidosFontes[nome] = apelido;
                    Console.WriteLine($"[*] Apelido definido para {nome}: '{apelido}'");
                }

                SseManager.NotificarClientes();
                return Results.Json(new { status = "ok" });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { status = "error", message = ex.Message });
            }
        });

        // API: Definir cor de fundo do mosaico
        app.MapPost("/api/definir_fundo/{cor}", (string cor) =>
        {
            lock (AppConfig.LockFontes)
            {
                if (AppConfig.CoresBackground.ContainsKey(cor))
                {
                    AppConfig.CorFundoAtual = cor;
                    Console.WriteLine($"[*] Fundo solicitado: {cor}");
                    SseManager.NotificarClientes();
                    return Results.Json(new { status = "ok" });
                }
            }
            return Results.BadRequest(new { status = "error", message = "Cor invalida." });
        });

        // API: Obter configuracoes gerais
        app.MapGet("/api/configuracoes", () =>
        {
            return Results.Json(new
            {
                formatoAudio = AppConfig.FormatoAudioAtual,
                corFundo = AppConfig.CorFundoAtual,
                apagarTemporarios = AppConfig.ApagarTemporarios,
                qualidadeGravacao = AppConfig.QualidadeGravacao,
                habilitarLogsDiagnostico = AppConfig.HabilitarLogsDiagnostico,
                mosaicoVertical = AppConfig.MosaicoVertical,
                paddingMosaico = AppConfig.PaddingMosaico
            });
        });

        // API: Definir o padding do mosaico (0-100px)
        app.MapPost("/api/configuracoes/definir_padding/{valor}", (int valor) =>
        {
            if (valor < 0 || valor > 100)
            {
                return Results.BadRequest(new { status = "error", message = "Padding invalido. Deve ser entre 0 e 100." });
            }
            AppConfig.PaddingMosaico = valor;
            Console.WriteLine($"[*] Padding do mosaico definido para: {valor}px");
            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok", paddingMosaico = valor });
        });

        // API: Definir volume individual da fonte (0-150%)
        app.MapPost("/api/audio/volume/{*parametros}", (string parametros) =>
        {
            if (string.IsNullOrEmpty(parametros))
            {
                return Results.BadRequest(new { status = "error", message = "Parametros invalidos." });
            }

            int lastSlash = parametros.LastIndexOf('/');
            if (lastSlash == -1)
            {
                return Results.BadRequest(new { status = "error", message = "Formato invalido. Use /api/audio/volume/NOME/VALOR" });
            }

            string nome = parametros.Substring(0, lastSlash);
            string valStr = parametros.Substring(lastSlash + 1);

            if (!int.TryParse(valStr, out int valor) || valor < 0 || valor > 150)
            {
                return Results.BadRequest(new { status = "error", message = "Volume invalido. Deve ser entre 0 e 150." });
            }

            lock (AppConfig.LockVolumes)
            {
                AppConfig.VolumesFontes[nome] = valor / 100.0f;
                Console.WriteLine($"[*] Volume de '{nome}' definido para: {valor}%");
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // API: Definir se o mosaico principal corta câmeras na vertical
        app.MapPost("/api/configuracoes/definir_mosaico_vertical/{valor}", (bool valor) =>
        {
            AppConfig.MosaicoVertical = valor;
            Console.WriteLine($"[*] Mosaico vertical alterado para: {valor}");
            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok", mosaicoVertical = valor });
        });

        // API: Definir formato de audio global (pcm / aac)
        app.MapPost("/api/configuracoes/definir_audio/{formato}", (string formato) =>
        {
            if (formato == "pcm" || formato == "aac")
            {
                AppConfig.FormatoAudioAtual = formato;
                Console.WriteLine($"[*] Formato de audio global alterado para: {formato}");
                SseManager.NotificarClientes();
                return Results.Json(new { status = "ok", formato = formato });
            }
            return Results.BadRequest(new { status = "error", message = "Formato invalido. Escolha 'pcm' ou 'aac'." });
        });

        // API: Definir se apaga arquivos temporarios
        app.MapPost("/api/configuracoes/definir_temporarios/{valor}", (bool valor) =>
        {
            AppConfig.ApagarTemporarios = valor;
            Console.WriteLine($"[*] Apagar arquivos temporários alterado para: {valor}");
            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok", apagarTemporarios = valor });
        });

        // API: Definir se habilita logs de diagnostico de sincronia
        app.MapPost("/api/configuracoes/definir_diagnostico/{valor}", (bool valor) =>
        {
            AppConfig.HabilitarLogsDiagnostico = valor;
            Console.WriteLine($"[*] Exibição de logs de diagnóstico alterada para: {valor}");
            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok", habilitarLogsDiagnostico = valor });
        });

        // API: Definir qualidade de gravação global (alta / media / baixa)
        app.MapPost("/api/configuracoes/definir_qualidade/{qualidade}", (string qualidade) =>
        {
            if (qualidade == "alta" || qualidade == "media" || qualidade == "baixa")
            {
                AppConfig.QualidadeGravacao = qualidade;
                Console.WriteLine($"[*] Qualidade de gravação global alterada para: {qualidade}");
                SseManager.NotificarClientes();
                return Results.Json(new { status = "ok", qualidade = qualidade });
            }
            return Results.BadRequest(new { status = "error", message = "Qualidade invalida. Escolha 'alta', 'media' ou 'baixa'." });
        });

        // API: Capturar thumbnail estática (Preview Card)
        app.MapGet("/api/preview/{*nome}", (string nome) =>
        {
            ReceptorNDI? rec;
            lock (AppConfig.LockFontes)
            {
                AppConfig.ReceptoresAtivos.TryGetValue(nome, out rec);
            }

            bool receptorTemporario = false;
            if (rec == null)
            {
                lock (AppConfig.LockFontes)
                {
                    if (!AppConfig.FontesNaRede.Contains(nome))
                    {
                        return Results.NoContent();
                    }
                }

                try
                {
                    Console.WriteLine($"[*] Criando receptor temporário para obter 1 frame de preview: '{nome}'");
                    rec = new ReceptorNDI(nome);
                    receptorTemporario = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao criar receptor temporário para preview: {ex.Message}");
                    return Results.NoContent();
                }
            }

            Mat? frame = rec.ObterFrame();

            // Se o primeiro frame ainda não chegou, aguarda até 2000ms (temporário) ou 500ms (ativo)
            if (frame == null)
            {
                int tentativas = receptorTemporario ? 20 : 5;
                while (tentativas-- > 0 && frame == null)
                {
                    Thread.Sleep(100);
                    frame = rec.ObterFrame();
                }
            }

            if (receptorTemporario && rec != null)
            {
                try
                {
                    rec.Parar();
                    Console.WriteLine($"[*] Receptor temporário para preview finalizado: '{nome}'");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao fechar receptor temporário: {ex.Message}");
                }
            }

            if (frame == null) return Results.NoContent();

            using (frame)
            {
                using var bgr = new Mat();
                Cv2.CvtColor(frame, bgr, ColorConversionCodes.BGRA2BGR);

                using var thumb = new Mat();
                Cv2.Resize(bgr, thumb, new OpenCvSharp.Size(240, 135), 0, 0, InterpolationFlags.Linear);

                byte[] jpegBytes = thumb.ImEncode(".jpg", new[] { (int)ImwriteFlags.JpegQuality, 80 });
                return Results.Bytes(jpegBytes, "image/jpeg");
            }
        });

        // SSE: Stream de Eventos em Tempo Real para o Painel Web
        app.MapGet("/api/eventos", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            SseManager.AdicionarCliente(context.Response);

            await context.Response.WriteAsync("data: update\n\n");
            await context.Response.Body.FlushAsync();

            var tcs = new TaskCompletionSource<bool>();
            context.RequestAborted.Register(() => {
                SseManager.RemoverCliente(context.Response);
                tcs.TrySetResult(true);
            });

            while (!context.RequestAborted.IsCancellationRequested)
            {
                await Task.Delay(25000);
                try
                {
                    await context.Response.WriteAsync(": heartbeat\n\n");
                    await context.Response.Body.FlushAsync();
                }
                catch
                {
                    break;
                }
            }

            await tcs.Task;
        });

        // Roda a aplicação
        Console.WriteLine("[*] Servidor web iniciado na porta 8634...");
        app.Run();

        // Cleanup
        NdiScanner.Parar();
        VideoEngine.Parar();
        AppConfig.MixerGlobal.Parar();
        SseManager.PararEnvioVu();
        
        lock (AppConfig.LockFontes)
        {
            foreach (var rec in AppConfig.ReceptoresAtivos.Values)
            {
                rec.Parar();
            }
            AppConfig.ReceptoresAtivos.Clear();
        }
    }

    static string? ObterCaminhoFisico(string subcaminho)
    {
        var caminhos = new[]
        {
            Path.Combine(AppContext.BaseDirectory, subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", subcaminho),
            subcaminho
        };

        foreach (var caminho in caminhos)
        {
            if (File.Exists(caminho)) return caminho;
        }
        return null;
    }
}
