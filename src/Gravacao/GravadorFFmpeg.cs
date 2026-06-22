using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using OpenCvSharp;
using NewTek;
using NewTek.NDI;

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
    private readonly ConcurrentQueue<byte[]> _audioQueue = new();
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
            SseManager.LogAtividade($"[Gravador] Codificação de vídeo para '{NomeFonte}' iniciada usando {(usarNVENC ? "GPU (NVENC)" : "CPU (libx264)")}.", "normal");

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
                            SseManager.LogAtividade($"[Gravador] Falha ao codificar via GPU para '{NomeFonte}'. Iniciando fallback para CPU (libx264)...", "aviso");
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
                SseManager.LogAtividade($"[Gravador] Erro ao iniciar codificação via GPU para '{NomeFonte}'. Iniciando fallback para CPU (libx264)...", "aviso");
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
                        AppConfig.ProcessosMuxing.TryRemove(NomeFonte, out var discard);
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
                        AppConfig.ProcessosMuxing.TryRemove(NomeFonte, out var discard);
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
                    AppConfig.ProcessosMuxing.TryRemove(NomeFonte, out var discard);
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
