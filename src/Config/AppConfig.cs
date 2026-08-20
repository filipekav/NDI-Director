using System.Text.Json;
using System.Collections.Concurrent;

// ===========================================================================
// CONFIGURAÇÃO E ESTADO GLOBAL
// ===========================================================================
public static class AppConfig
{
    public static List<string> FontesNaRede = new();
    public static ConcurrentDictionary<string, ReceptorNDI> ReceptoresAtivos = new();
    public static ConcurrentDictionary<string, ReceptorNDI> ReceptoresPreview = new();
    public static string?[] OrdemReceptores = new string?[4];
    public static string? FonteHighlight = null;
    public static string? FonteSolo = null;
    public static ConcurrentDictionary<string, string> ApelidosFontes = new();
    public static string CorFundoAtual = "verde";
    public static string FormatoAudioAtual = "aac"; // "pcm" ou "aac"
    public static bool ApagarTemporarios = true;
    public static string QualidadeGravacao = "media"; // "alta", "media" ou "baixa"
    public static bool HabilitarLivePreview = true;
    public static bool HabilitarLogsDiagnostico = false; // Silencia por padrão logs verbosos de progresso e sincronia
    public static bool MosaicoVertical = false;
    public static int PaddingMosaico = 20;
    public static int LimiteSessoesNvenc = 8;
    public static string MotorVideo = "cpu"; // "cpu" (OpenCV+GDI+) ou "gpu" (DirectX 11+Direct2D)
    public static int CanvasLarguraHorizontal = 1920;
    public static int CanvasAlturaHorizontal = 850;
    public static int CanvasLarguraVertical = 550;
    public static int CanvasAlturaVertical = 850;
    public static ConcurrentDictionary<string, float> VolumesFontes = new();
    public static ConcurrentDictionary<string, int> NiveisVu = new();
    
    // Configurações e Estado do Auto Lip-Sync (A/V Sync)
    public static bool AutoLipSync = true;
    public static int AtrasoAudioManualMs = 0;
    public static int LatenciaVideoMedidaMs = 200;
    private static double _latenciaFiltradaMs = 200.0;
    public static readonly object LockLipSync = new();
    
    public static readonly object LockFontes = new();
    public static readonly object LockVolumes = new();
    public static readonly object LockVu = new();
    
    // Gravadores individuais por FFmpeg acelerado por NVIDIA GPU
    public static ConcurrentDictionary<string, GravadorFFmpeg> GravadoresAtivos = new();
    public static readonly object LockGravadores = new();
    
    // Muxing em andamento
    public static ConcurrentDictionary<string, MuxingStatus> ProcessosMuxing = new();
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

    private const string CONFIG_FILE = "ndi_director_config.json";
    private static readonly object LockConfig = new();

    public static void AtualizarLatenciaVideoMedida(int latenciaMs)
    {
        if (latenciaMs <= 0 || latenciaMs > 2000) return;

        lock (LockLipSync)
        {
            // Filtro IIR passa-baixa (peso 0.05) para amortecer micro-flutuações e dar estabilidade
            _latenciaFiltradaMs = (_latenciaFiltradaMs * 0.95) + (latenciaMs * 0.05);
            LatenciaVideoMedidaMs = (int)Math.Round(_latenciaFiltradaMs);
        }
    }

    public static int ObterAtrasoAudioEfetivoMs()
    {
        lock (LockLipSync)
        {
            if (AutoLipSync)
            {
                return Math.Clamp(LatenciaVideoMedidaMs + AtrasoAudioManualMs, 0, 1000);
            }
            else
            {
                return Math.Clamp(AtrasoAudioManualMs, 0, 1000);
            }
        }
    }

    public static void SalvarConfiguracoes()
    {
        lock (LockConfig)
        {
            try
            {
                var data = new ConfigData
                {
                    CorFundoAtual = CorFundoAtual,
                    FormatoAudioAtual = FormatoAudioAtual,
                    ApagarTemporarios = ApagarTemporarios,
                    QualidadeGravacao = QualidadeGravacao,
                    HabilitarLivePreview = HabilitarLivePreview,
                    HabilitarLogsDiagnostico = HabilitarLogsDiagnostico,
                    MosaicoVertical = MosaicoVertical,
                    PaddingMosaico = PaddingMosaico,
                    CanvasLarguraHorizontal = CanvasLarguraHorizontal,
                    CanvasAlturaHorizontal = CanvasAlturaHorizontal,
                    CanvasLarguraVertical = CanvasLarguraVertical,
                    CanvasAlturaVertical = CanvasAlturaVertical,
                    LimiteSessoesNvenc = LimiteSessoesNvenc,
                    ApelidosFontes = new Dictionary<string, string>(ApelidosFontes),
                    VolumesFontes = new Dictionary<string, float>(VolumesFontes),
                    MotorVideo = MotorVideo,
                    AutoLipSync = AutoLipSync,
                    AtrasoAudioManualMs = AtrasoAudioManualMs
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(data, options);
                
                string tempFile = CONFIG_FILE + ".tmp";
                File.WriteAllText(tempFile, json);
                
                if (File.Exists(CONFIG_FILE))
                {
                    File.Delete(CONFIG_FILE);
                }
                File.Move(tempFile, CONFIG_FILE);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Erro ao salvar configurações: {ex.Message}");
            }
        }
    }

    public static void CarregarConfiguracoes()
    {
        lock (LockConfig)
        {
            try
            {
                if (!File.Exists(CONFIG_FILE)) return;

                string json = File.ReadAllText(CONFIG_FILE);
                var data = JsonSerializer.Deserialize<ConfigData>(json);
                if (data == null) return;

                CorFundoAtual = data.CorFundoAtual ?? "verde";
                FormatoAudioAtual = data.FormatoAudioAtual ?? "aac";
                ApagarTemporarios = data.ApagarTemporarios;
                QualidadeGravacao = data.QualidadeGravacao ?? "media";
                HabilitarLivePreview = data.HabilitarLivePreview;
                HabilitarLogsDiagnostico = data.HabilitarLogsDiagnostico;
                MosaicoVertical = data.MosaicoVertical;
                PaddingMosaico = data.PaddingMosaico;
                CanvasLarguraHorizontal = data.CanvasLarguraHorizontal;
                CanvasAlturaHorizontal = data.CanvasAlturaHorizontal;
                CanvasLarguraVertical = data.CanvasLarguraVertical;
                CanvasAlturaVertical = data.CanvasAlturaVertical;
                LimiteSessoesNvenc = data.LimiteSessoesNvenc > 0 ? data.LimiteSessoesNvenc : 8;
                MotorVideo = (data.MotorVideo == "gpu") ? "gpu" : "cpu";
                AutoLipSync = data.AutoLipSync;
                AtrasoAudioManualMs = data.AtrasoAudioManualMs;

                ApelidosFontes.Clear();
                if (data.ApelidosFontes != null)
                {
                    foreach (var kvp in data.ApelidosFontes)
                    {
                        ApelidosFontes[kvp.Key] = kvp.Value;
                    }
                }

                VolumesFontes.Clear();
                if (data.VolumesFontes != null)
                {
                    foreach (var kvp in data.VolumesFontes)
                    {
                        VolumesFontes[kvp.Key] = kvp.Value;
                    }
                }

                Console.WriteLine($"[*] Configurações carregadas com sucesso de '{CONFIG_FILE}' (AutoLipSync: {AutoLipSync}, Offset: {AtrasoAudioManualMs}ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Erro ao carregar configurações: {ex.Message}. Mantendo padrões.");
            }
        }
    }
}
