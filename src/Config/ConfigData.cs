public class ConfigData
{
    public string CorFundoAtual { get; set; } = "verde";
    public string FormatoAudioAtual { get; set; } = "aac";
    public bool ApagarTemporarios { get; set; } = true;
    public string QualidadeGravacao { get; set; } = "media";
    public bool HabilitarLivePreview { get; set; } = true;
    public bool HabilitarLogsDiagnostico { get; set; } = false;
    public bool MosaicoVertical { get; set; } = false;
    public int PaddingMosaico { get; set; } = 20;
    public int CanvasLarguraHorizontal { get; set; } = 1920;
    public int CanvasAlturaHorizontal { get; set; } = 850;
    public int CanvasLarguraVertical { get; set; } = 550;
    public int CanvasAlturaVertical { get; set; } = 850;
    public int LimiteSessoesNvenc { get; set; } = 8;
    public Dictionary<string, string> ApelidosFontes { get; set; } = new();
    public Dictionary<string, float> VolumesFontes { get; set; } = new();
}
