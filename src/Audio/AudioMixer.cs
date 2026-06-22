using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// MIXER DE ÁUDIO NDI EM TEMPO REAL
// ===========================================================================
public class AudioMixer
{
    public const int SampleRateSaida = 48000;
    public const int CanaisSaida = 2;
    public const int TamanhoBloco = 960; // 20ms de áudio a 48kHz
    private const double IntervaloBlocoMs = 20.0;

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

    // Fila de blocos mixados prontos para a saída NDI (contém arrays de float de tamanho 2 * 960 = 1920)
    public readonly ConcurrentQueue<float[]> FilaSaida = new();

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

            // 2. Obter estado do mixer
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

            // 4. Adicionar aos buffers circulares
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

                // Lógica de compensação de Clock Drift
                if (estado.L.Count > 14400)
                {
                    int descartar = estado.L.Count - 4800;
                    for (int i = 0; i < descartar; i++)
                    {
                        estado.L.Dequeue();
                        estado.R.Dequeue();
                    }
                    estado.AplicarFadeInProximo = true;
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
            if (_buffers.TryGetValue(nomeFonte, out var estado))
            {
                int disponivel = estado.L.Count;

                // 1. Se estiver no estado de Buffering, aguarda o buffer encher até um nível seguro
                if (estado.EmBuffering)
                {
                    // Limite seguro para recomeçar a reprodução: 2880 amostras (~60ms de áudio a 48kHz)
                    int limiteGatilhoBuffering = 2880; 
                    if (disponivel >= limiteGatilhoBuffering)
                    {
                        estado.EmBuffering = false;
                        estado.AplicarFadeInProximo = true;
                    }
                    else
                    {
                        estado.BlocoAnteriorFoiSilencio = true;
                        estado.TerminouEmFadeOut = true; // Garante que aplicará fade-in quando voltar
                        return (leftSamples, rightSamples);
                    }
                }

                // 2. Se o buffer está completamente zerado, entra em buffering imediatamente
                if (disponivel == 0)
                {
                    estado.EmBuffering = true;
                    estado.BlocoAnteriorFoiSilencio = true;
                    estado.TerminouEmFadeOut = true;
                    return (leftSamples, rightSamples);
                }

                // 3. Caso de sobressalto / underflow parcial
                if (disponivel < quantidade)
                {
                    // Se o sobressalto for muito severo (menos de 480 amostras / 10ms),
                    // lemos o que sobrou e entramos em buffering para restabelecer a segurança.
                    // Caso contrário (>= 480), tentamos tocar o que temos e preencher o final com silêncio suave,
                    // mantendo a reprodução ativa sem entrar em modo buffering drástico.
                    if (disponivel < 480)
                    {
                        estado.EmBuffering = true;
                    }

                    int tamanhoLer = disponivel; // Lê todo o restante disponível
                    for (int i = 0; i < tamanhoLer; i++)
                    {
                        leftSamples[i] = estado.L.Dequeue();
                        rightSamples[i] = estado.R.Dequeue();
                    }

                    // Aplica Fade-In se o bloco anterior terminou em silêncio/fade-out
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

                    // Aplica Fade-Out suave nas últimas amostras válidas lidas para evitar clique físico
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

                // 4. Fluxo normal estável: temos amostras suficientes para preencher o bloco inteiro
                for (int i = 0; i < quantidade; i++)
                {
                    leftSamples[i] = estado.L.Dequeue();
                    rightSamples[i] = estado.R.Dequeue();
                }

                // Aplica Fade-In se o bloco anterior terminou em silêncio/fade-out
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
