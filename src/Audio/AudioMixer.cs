using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// MIXER DE ÁUDIO NDI EM TEMPO REAL COM NORMALIZADOR ELÁSTICO & LIP-SYNC
// ===========================================================================
public class AudioMixer
{
    public const int SampleRateSaida = 48000;
    public const int CanaisSaida = 2;
    public const int TamanhoBloco = 960; // 20ms de áudio a 48kHz
    public const double IntervaloBlocoMs = 20.0;
    public const int TargetBufferSamples = 1920; // Alvo de estabilidade: 40ms por participante

    private class FonteAudioState
    {
        public Queue<float> L { get; } = new(480000);
        public Queue<float> R { get; } = new(480000);
        public bool EmBuffering { get; set; } = true;
        public bool AplicarFadeInProximo { get; set; } = false;
        public bool BlocoAnteriorFoiSilencio { get; set; } = true;
        public bool TerminouEmFadeOut { get; set; } = true;
        public double FaseResample { get; set; } = 0.0;
        public float UltimaAmostraL { get; set; } = 0.0f;
        public float UltimaAmostraR { get; set; } = 0.0f;
        public bool TemUltimaAmostra { get; set; } = false;
    }

    private readonly Dictionary<string, FonteAudioState> _buffers = new();
    private readonly object _lockBuffers = new();

    // Fila de atraso de Lip-Sync (calibrado dinamicamente com a latência de vídeo)
    private readonly Queue<float[]> _filaDelayLipSync = new();
    private readonly object _lockDelayQueue = new();

    // Fila de blocos mixados e atrasados prontos para a saída NDI
    public readonly ConcurrentQueue<float[]> FilaSaidaNdi = new();

    private Thread? _mixerThread;
    private Thread? _audioSenderThread;
    private bool _running = false;

    private IntPtr _pNdiSendAudio = IntPtr.Zero;

    public void Iniciar()
    {
        if (_running) return;
        _running = true;

        // 1. Cria o Sender NDI dedicado de áudio (MESA_NDI_AUDIO)
        var sendSettings = new NDIlib.send_create_t
        {
            p_ndi_name = Marshal.StringToHGlobalAnsi("MESA_NDI_AUDIO"),
            clock_video = false,
            clock_audio = false
        };
        _pNdiSendAudio = NDIlib.send_create(ref sendSettings);
        Marshal.FreeHGlobal(sendSettings.p_ndi_name);

        if (_pNdiSendAudio == IntPtr.Zero)
        {
            Console.WriteLine("[!] Erro: Não foi possível criar o sender NDI dedicado 'MESA_NDI_AUDIO'.");
        }
        else
        {
            Console.WriteLine("[*] Sender NDI dedicado 'MESA_NDI_AUDIO' inicializado.");
        }

        // 2. Inicia a Thread de Mixagem (20ms)
        _mixerThread = new Thread(MixerLoop)
        {
            IsBackground = true,
            Name = "NDI_Audio_Mixer",
            Priority = ThreadPriority.AboveNormal
        };
        _mixerThread.Start();

        // 3. Inicia a Thread Dedicada de Transmissão NDI (20ms estrito)
        _audioSenderThread = new Thread(AudioSenderLoop)
        {
            IsBackground = true,
            Name = "NDI_Audio_Sender",
            Priority = ThreadPriority.Highest
        };
        _audioSenderThread.Start();

        Console.WriteLine("[*] Mixer de áudio NDI iniciado com sucesso (48kHz, Estéreo, Auto-LipSync ativo).");
    }

    public void Parar()
    {
        _running = false;
        _mixerThread?.Join(1000);
        _audioSenderThread?.Join(1000);

        lock (_lockBuffers)
        {
            _buffers.Clear();
        }

        lock (_lockDelayQueue)
        {
            _filaDelayLipSync.Clear();
        }

        while (FilaSaidaNdi.TryDequeue(out _)) { }

        if (_pNdiSendAudio != IntPtr.Zero)
        {
            NDIlib.send_destroy(_pNdiSendAudio);
            _pNdiSendAudio = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Retorna a telemetria do buffer de áudio de cada participante em milissegundos para monitoramento na UI.
    /// </summary>
    public Dictionary<string, (int BufferMs, string Status)> ObterStatusBuffers()
    {
        var status = new Dictionary<string, (int BufferMs, string Status)>();
        lock (_lockBuffers)
        {
            foreach (var kvp in _buffers)
            {
                int samples = kvp.Value.L.Count;
                int ms = (int)Math.Round((double)samples / (SampleRateSaida / 1000.0));
                string desc = (ms >= 30 && ms <= 60) ? "Estável" : (ms > 60 ? "Alinhando" : "Buffering");
                status[kvp.Key] = (ms, desc);
            }
        }
        return status;
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

            // 2. Obter estado do mixer para a fonte
            FonteAudioState estado;
            lock (_lockBuffers)
            {
                if (!_buffers.TryGetValue(nomeFonte, out var est))
                {
                    est = new FonteAudioState();
                    _buffers[nomeFonte] = est;
                }
                estado = est!;
            }

            // 3. Reamostragem (Resampling) linear contínua com fase acumulada
            float[] leftResampled;
            float[] rightResampled;

            if (sampleRate != SampleRateSaida)
            {
                double passo = (double)sampleRate / SampleRateSaida;
                var listL = new List<float>();
                var listR = new List<float>();

                lock (_lockBuffers)
                {
                    double fase = estado.FaseResample;

                    while (fase < noSamples - 1)
                    {
                        int idxLow = (int)Math.Floor(fase);
                        double weight = fase - idxLow;

                        float amostraL_Low, amostraR_Low;
                        float amostraL_High, amostraR_High;

                        if (idxLow < 0)
                        {
                            amostraL_Low = estado.TemUltimaAmostra ? estado.UltimaAmostraL : left[0];
                            amostraR_Low = estado.TemUltimaAmostra ? estado.UltimaAmostraR : right[0];
                        }
                        else
                        {
                            amostraL_Low = left[idxLow];
                            amostraR_Low = right[idxLow];
                        }

                        int idxHigh = idxLow + 1;
                        if (idxHigh < 0)
                        {
                            amostraL_High = estado.TemUltimaAmostra ? estado.UltimaAmostraL : left[0];
                            amostraR_High = estado.TemUltimaAmostra ? estado.UltimaAmostraR : right[0];
                        }
                        else
                        {
                            amostraL_High = left[idxHigh];
                            amostraR_High = right[idxHigh];
                        }

                        float outL = (float)((1.0 - weight) * amostraL_Low + weight * amostraL_High);
                        float outR = (float)((1.0 - weight) * amostraR_Low + weight * amostraR_High);

                        listL.Add(outL);
                        listR.Add(outR);

                        fase += passo;
                    }

                    estado.FaseResample = fase - noSamples;
                    estado.UltimaAmostraL = left[noSamples - 1];
                    estado.UltimaAmostraR = right[noSamples - 1];
                    estado.TemUltimaAmostra = true;
                }

                leftResampled = listL.ToArray();
                rightResampled = listR.ToArray();
            }
            else
            {
                leftResampled = left;
                rightResampled = right;

                lock (_lockBuffers)
                {
                    estado.FaseResample = 0.0;
                    estado.UltimaAmostraL = left[noSamples - 1];
                    estado.UltimaAmostraR = right[noSamples - 1];
                    estado.TemUltimaAmostra = true;
                }
            }

            // 4. Adicionar aos buffers circulares da fonte (sem descartes abruptos de 200ms!)
            lock (_lockBuffers)
            {
                int maxCapacity = 480000;
                for (int i = 0; i < leftResampled.Length; i++)
                {
                    if (estado.L.Count >= maxCapacity)
                    {
                        estado.L.Dequeue();
                        estado.R.Dequeue();
                    }
                    estado.L.Enqueue(leftResampled[i]);
                    estado.R.Enqueue(rightResampled[i]);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Erro ao processar áudio no mixer para a fonte '{nomeFonte}': {ex.Message}");
        }
    }

    /// <summary>
    /// Lê a quantidade necessária de amostras aplicando compensação elástica de clock drift e rajadas do Teams.
    /// Mantém o buffer de cada participante suavemente travado no alvo de 40ms (1.920 amostras).
    /// </summary>
    private (float[] L, float[] R) ObterAmostrasFonte(string nomeFonte, int quantidade)
    {
        float[] leftSamples = new float[quantidade];
        float[] rightSamples = new float[quantidade];

        lock (_lockBuffers)
        {
            if (_buffers.TryGetValue(nomeFonte, out var estado))
            {
                int disponivel = estado.L.Count;

                // 1. Se estiver no estado de inicialização / Buffering, aguarda acumular os 40ms
                if (estado.EmBuffering)
                {
                    if (disponivel >= TargetBufferSamples)
                    {
                        estado.EmBuffering = false;
                        estado.AplicarFadeInProximo = true;
                    }
                    else
                    {
                        estado.BlocoAnteriorFoiSilencio = true;
                        estado.TerminouEmFadeOut = true;
                        return (leftSamples, rightSamples);
                    }
                }

                // 2. Se o buffer está completamente zerado
                if (disponivel == 0)
                {
                    estado.EmBuffering = true;
                    estado.BlocoAnteriorFoiSilencio = true;
                    estado.TerminouEmFadeOut = true;
                    return (leftSamples, rightSamples);
                }

                // 3. Caso de sobressalto / underflow (menos de 960 amostras disponíveis)
                if (disponivel < quantidade)
                {
                    int tamanhoLer = disponivel;
                    for (int i = 0; i < tamanhoLer; i++)
                    {
                        leftSamples[i] = estado.L.Dequeue();
                        rightSamples[i] = estado.R.Dequeue();
                    }

                    if (estado.AplicarFadeInProximo || estado.TerminouEmFadeOut)
                    {
                        int fadeLenIn = Math.Min(128, tamanhoLer);
                        for (int i = 0; i < fadeLenIn; i++)
                        {
                            float fator = (float)i / fadeLenIn;
                            leftSamples[i] *= fator;
                            rightSamples[i] *= fator;
                        }
                        estado.AplicarFadeInProximo = false;
                    }

                    int fadeLenOut = Math.Min(128, tamanhoLer);
                    if (fadeLenOut > 0)
                    {
                        int startIndex = tamanhoLer - fadeLenOut;
                        for (int i = 0; i < fadeLenOut; i++)
                        {
                            float fator = 1.0f - ((float)i / fadeLenOut);
                            leftSamples[startIndex + i] *= fator;
                            rightSamples[startIndex + i] *= fator;
                        }
                    }

                    estado.TerminouEmFadeOut = true;
                    estado.BlocoAnteriorFoiSilencio = true;
                    return (leftSamples, rightSamples);
                }

                // 4. Fluxo Normal: Cálculo do Deslocamento Elástico (Zero-Drop Dynamic Rate Control)
                // Se a fila acumulada estiver acima do alvo (1920 amostras / 40ms), consome suavemente amostras extras
                // Se a fila estiver abaixo, consome suavemente menos amostras.
                int erroAmostras = disponivel - TargetBufferSamples;
                int amostrasParaConsumir = quantidade;

                if (erroAmostras > 480) // Mais de 10ms acima do alvo de 40ms
                {
                    // Acelera a leitura em ~0.1% a 0.2% consumindo 1 ou 2 amostras a mais da fila
                    int extras = (erroAmostras > 2400) ? 3 : ((erroAmostras > 960) ? 2 : 1);
                    amostrasParaConsumir = Math.Min(disponivel, quantidade + extras);
                }
                else if (erroAmostras < -480) // Mais de 10ms abaixo do alvo de 40ms
                {
                    // Desacelera a leitura em ~0.1% consumindo 1 amostra a menos da fila
                    amostrasParaConsumir = Math.Max(1, quantidade - 1);
                }

                if (amostrasParaConsumir == quantidade)
                {
                    // Leitura direta 1:1
                    for (int i = 0; i < quantidade; i++)
                    {
                        leftSamples[i] = estado.L.Dequeue();
                        rightSamples[i] = estado.R.Dequeue();
                    }
                }
                else
                {
                    // Interpolação suave para ler 'amostrasParaConsumir' e preencher exatamente 'quantidade' (960) saídas
                    float[] tempL = new float[amostrasParaConsumir];
                    float[] tempR = new float[amostrasParaConsumir];
                    for (int i = 0; i < amostrasParaConsumir; i++)
                    {
                        tempL[i] = estado.L.Dequeue();
                        tempR[i] = estado.R.Dequeue();
                    }

                    double step = (double)(amostrasParaConsumir - 1) / (quantidade - 1);
                    for (int i = 0; i < quantidade; i++)
                    {
                        double pos = i * step;
                        int idxLow = (int)pos;
                        int idxHigh = Math.Min(amostrasParaConsumir - 1, idxLow + 1);
                        float frac = (float)(pos - idxLow);

                        leftSamples[i] = tempL[idxLow] * (1.0f - frac) + tempL[idxHigh] * frac;
                        rightSamples[i] = tempR[idxLow] * (1.0f - frac) + tempR[idxHigh] * frac;
                    }
                }

                // Aplica Fade-In suave se vinha de silêncio
                if (estado.AplicarFadeInProximo || estado.TerminouEmFadeOut)
                {
                    estado.AplicarFadeInProximo = false;
                    estado.TerminouEmFadeOut = false;
                    int fadeLenIn = Math.Min(128, quantidade);
                    for (int i = 0; i < fadeLenIn; i++)
                    {
                        float fator = (float)i / fadeLenIn;
                        leftSamples[i] *= fator;
                        rightSamples[i] *= fator;
                    }
                }

                estado.BlocoAnteriorFoiSilencio = false;
                estado.TerminouEmFadeOut = false;
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

                // Envia bloco de silêncio para manter a esteira de Lip-Sync viva
                float[] blocoSilencio = new float[TamanhoBloco * 2];
                ProcessarFilaLipSync(blocoSilencio);
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
                        AppConfig.NiveisVu.TryRemove(k, out _);
                    }
                }

                foreach (var nome in fontesAtivas)
                {
                    float ganho = 1.0f;
                    lock (AppConfig.LockVolumes)
                    {
                        if (AppConfig.VolumesFontes.TryGetValue(nome, out float v))
                        {
                            ganho = v;
                        }
                    }

                    var (left, right) = ObterAmostrasFonte(nome, TamanhoBloco);

                    // VU Meter: Pico absoluto das amostras brutas
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

            // Passa o bloco mixado pelo compensador de atraso de Lip-Sync
            ProcessarFilaLipSync(blocoMixado);
        }
    }

    /// <summary>
    /// Aplica o atraso calibrado de Lip-Sync retendo os blocos necessários na memória
    /// para casar rigorosamente com a latência de renderização de vídeo da GPU/CPU.
    /// </summary>
    private void ProcessarFilaLipSync(float[] blocoMixado)
    {
        int atrasoEfetivoMs = AppConfig.ObterAtrasoAudioEfetivoMs();
        int blocosAtrasoDesejados = (int)Math.Round((double)atrasoEfetivoMs / IntervaloBlocoMs);

        lock (_lockDelayQueue)
        {
            _filaDelayLipSync.Enqueue(blocoMixado);

            while (_filaDelayLipSync.Count > blocosAtrasoDesejados)
            {
                var blocoAtrasado = _filaDelayLipSync.Dequeue();

                if (FilaSaidaNdi.Count < 50)
                {
                    FilaSaidaNdi.Enqueue(blocoAtrasado);
                }
                else
                {
                    FilaSaidaNdi.TryDequeue(out _);
                    FilaSaidaNdi.Enqueue(blocoAtrasado);
                }
            }
        }
    }

    /// <summary>
    /// Thread dedicada de alta prioridade que envia pacotes NDI de áudio a cada 20.0ms exatos.
    /// Garante que o stream 'MESA_NDI_AUDIO' chegue liso e sem rajadas no OBS.
    /// </summary>
    private void AudioSenderLoop()
    {
        IntPtr pAudioBufferNativo = Marshal.AllocHGlobal(TamanhoBloco * CanaisSaida * sizeof(float));
        float[] blocoSilencio = new float[TamanhoBloco * CanaisSaida];

        var sw = Stopwatch.StartNew();
        double proximaTransmissaoMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            double agoraMs = sw.Elapsed.TotalMilliseconds;
            if (agoraMs < proximaTransmissaoMs)
            {
                double esperaMs = proximaTransmissaoMs - agoraMs;
                if (esperaMs > 1.0)
                {
                    Thread.Sleep((int)esperaMs);
                }
                else
                {
                    Thread.SpinWait(50);
                }
                continue;
            }

            proximaTransmissaoMs += IntervaloBlocoMs;

            if (_pNdiSendAudio == IntPtr.Zero) continue;

            float[]? blocoEnviar;
            if (!FilaSaidaNdi.TryDequeue(out blocoEnviar) || blocoEnviar == null)
            {
                blocoEnviar = blocoSilencio;
            }

            Marshal.Copy(blocoEnviar, 0, pAudioBufferNativo, blocoEnviar.Length);

            long timecodeAudio = DateTime.UtcNow.Ticks;

            var audioFrame = new NDIlib.audio_frame_v2_t
            {
                sample_rate = SampleRateSaida,
                no_channels = CanaisSaida,
                no_samples = TamanhoBloco,
                timecode = timecodeAudio,
                p_data = pAudioBufferNativo,
                channel_stride_in_bytes = TamanhoBloco * sizeof(float)
            };

            NDIlib.send_send_audio_v2(_pNdiSendAudio, ref audioFrame);
        }

        Marshal.FreeHGlobal(pAudioBufferNativo);
    }
}
