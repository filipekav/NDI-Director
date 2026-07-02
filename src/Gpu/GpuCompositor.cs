using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DWrite = Vortice.DirectWrite;

// ===========================================================================
// COMPOSITOR GPU (DIRECT3D 11 HEADLESS + DIRECT2D + DIRECTWRITE)
// ===========================================================================
/// <summary>
/// Classe que encapsula um dispositivo DirectX 11 headless (sem janela) para
/// composição de vídeo em hardware, substituindo OpenCV + GDI+ na CPU.
/// </summary>
public sealed class GpuCompositor : IDisposable
{
    // --- Direct3D 11 ---
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _canvasTexture;
    private ID3D11Texture2D? _stagingTexture;

    // --- Direct2D ---
    private ID2D1Factory? _d2dFactory;
    private ID2D1RenderTarget? _renderTarget;

    // --- DirectWrite ---
    private DWrite.IDWriteFactory? _dwriteFactory;
    private string _customFontFamilyName = "Segoe UI";

    // --- Cache de TextFormats por tamanho ---
    private readonly Dictionary<int, DWrite.IDWriteTextFormat> _textFormatCache = new();

    // --- Tamanho atual ---
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool Inicializado { get; private set; } = false;

    /// <summary>
    /// Inicializa o dispositivo DirectX 11, Direct2D e DirectWrite.
    /// Retorna true se a inicialização for bem-sucedida, false caso contrário.
    /// </summary>
    public bool Inicializar(int width, int height)
    {
        try
        {
            Width = width;
            Height = height;

            // 1. Criar dispositivo D3D11 headless (sem HWND)
            var result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new D3DFeatureLevel[] { D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0 },
                out _device,
                out D3DFeatureLevel _,
                out _context
            );

            if (result.Failure || _device == null || _context == null)
            {
                Console.WriteLine("[!] GPU Compositor: Falha ao criar dispositivo D3D11 via hardware. Tentando WARP...");
                result = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    new D3DFeatureLevel[] { D3DFeatureLevel.Level_11_0 },
                    out _device,
                    out _,
                    out _context
                );

                if (result.Failure || _device == null || _context == null)
                {
                    Console.WriteLine("[!] GPU Compositor: Falha total ao criar dispositivo D3D11.");
                    return false;
                }
                Console.WriteLine("[*] GPU Compositor: Dispositivo D3D11 criado via WARP (software).");
            }
            else
            {
                Console.WriteLine("[*] GPU Compositor: Dispositivo D3D11 criado via hardware com sucesso.");
            }

            // 2. Criar texturas
            CriarTexturas(width, height);

            // 3. Inicializar Direct2D
            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded);

            // 4. Criar render target Direct2D sobre a textura DXGI
            CriarRenderTarget();

            // 5. Inicializar DirectWrite
            _dwriteFactory = DWrite.DWrite.DWriteCreateFactory<DWrite.IDWriteFactory>();

            // 6. Carregar fonte Anton customizada
            CarregarFonteAnton();

            Inicializado = true;
            Console.WriteLine($"[*] GPU Compositor: Inicializado com sucesso ({width}x{height}).");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] GPU Compositor: Erro fatal na inicialização: {ex.Message}");
            Dispose();
            return false;
        }
    }

    private void CriarTexturas(int width, int height)
    {
        _canvasTexture?.Dispose();
        _stagingTexture?.Dispose();

        var canvasDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        _canvasTexture = _device!.CreateTexture2D(canvasDesc);

        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        _stagingTexture = _device!.CreateTexture2D(stagingDesc);
    }

    private void CriarRenderTarget()
    {
        _renderTarget?.Dispose();

        using var dxgiSurface = _canvasTexture!.QueryInterface<IDXGISurface>();

        var rtProps = new RenderTargetProperties
        {
            Type = RenderTargetType.Default,
            PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            DpiX = 96f,
            DpiY = 96f,
            Usage = RenderTargetUsage.None,
            MinLevel = Vortice.Direct2D1.FeatureLevel.Default
        };

        _renderTarget = _d2dFactory!.CreateDxgiSurfaceRenderTarget(dxgiSurface, rtProps);
    }

    private void CarregarFonteAnton()
    {
        // No Vortice DirectWrite, carregar fontes customizadas a partir de arquivo
        // requer usar um custom font collection loader. Para simplicidade e robustez,
        // tentamos detectar se "Anton" está instalada no sistema, caso contrário usamos Segoe UI.
        var caminhosFonte = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ANTON-REGULAR.TTF"),
            Path.Combine(AppContext.BaseDirectory, "..\\assets\\ANTON-REGULAR.TTF"),
            Path.Combine(AppContext.BaseDirectory, "..\\..\\assets\\ANTON-REGULAR.TTF"),
            Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\assets\\ANTON-REGULAR.TTF"),
            "assets\\ANTON-REGULAR.TTF"
        };

        string? fontPath = caminhosFonte.FirstOrDefault(File.Exists);

        if (!string.IsNullOrEmpty(fontPath) && _dwriteFactory != null)
        {
            try
            {
                // Tentar usar a API nativa AddFontResourceEx para registrar temporariamente a fonte no sistema
                int result = AddFontResourceEx(Path.GetFullPath(fontPath), FR_PRIVATE, IntPtr.Zero);
                if (result > 0)
                {
                    _customFontFamilyName = "Anton";
                    Console.WriteLine($"[*] GPU Compositor: Fonte 'Anton' registrada temporariamente via AddFontResourceEx ({fontPath}).");
                }
                else
                {
                    Console.WriteLine("[!] GPU Compositor: AddFontResourceEx retornou 0. Usando fonte fallback 'Segoe UI'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] GPU Compositor: Erro ao registrar fonte Anton: {ex.Message}. Usando fallback 'Segoe UI'.");
            }
        }
        else
        {
            Console.WriteLine("[!] GPU Compositor: Arquivo ANTON-REGULAR.TTF não encontrado. Usando fonte fallback 'Segoe UI'.");
        }
    }

    // P/Invoke para registro temporário de fontes no Windows
    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool RemoveFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);

    /// <summary>
    /// Redimensiona o canvas de composição se as dimensões mudaram.
    /// </summary>
    public void RedimensionarCanvas(int width, int height)
    {
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;

        CriarTexturas(width, height);
        CriarRenderTarget();

        foreach (var tf in _textFormatCache.Values) { try { tf.Dispose(); } catch { } }
        _textFormatCache.Clear();
    }

    /// <summary>
    /// Inicia um novo frame de composição e limpa o canvas com a cor de fundo.
    /// </summary>
    public void IniciarFrame(byte r, byte g, byte b, byte a)
    {
        float fa = a / 255f;
        _renderTarget!.BeginDraw();
        _renderTarget.Clear(new Color4(r / 255f, g / 255f, b / 255f, fa));
    }

    /// <summary>
    /// Carrega dados brutos BGRA para um bitmap Direct2D e desenha no canvas.
    /// </summary>
    public unsafe void DesenharFrame(IntPtr dadosBgra, int srcWidth, int srcHeight, int srcStride,
                                     float destX, float destY, float destW, float destH,
                                     bool cropVertical = false, float cropTargetAr = 0f)
    {
        if (dadosBgra == IntPtr.Zero || srcWidth <= 0 || srcHeight <= 0 || destW <= 0 || destH <= 0) return;

        try
        {
            var bmpProps = new BitmapProperties
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = 96f,
                DpiY = 96f
            };

            if (cropVertical && cropTargetAr > 0f)
            {
                // Calcular crop
                int cropX = 0, cropY = 0, cropW = srcWidth, cropH = srcHeight;
                float srcAr = (float)srcWidth / srcHeight;
                if (srcAr > cropTargetAr)
                {
                    cropW = (int)Math.Round(srcHeight * cropTargetAr);
                    cropX = (srcWidth - cropW) / 2;
                }
                else
                {
                    cropH = (int)Math.Round(srcWidth / cropTargetAr);
                    cropY = (srcHeight - cropH) / 2;
                }
                cropX = Math.Max(0, Math.Min(cropX, srcWidth - 1));
                cropY = Math.Max(0, Math.Min(cropY, srcHeight - 1));
                cropW = Math.Max(1, Math.Min(cropW, srcWidth - cropX));
                cropH = Math.Max(1, Math.Min(cropH, srcHeight - cropY));

                byte* srcPtr = (byte*)dadosBgra.ToPointer() + cropY * srcStride + cropX * 4;
                using var bmp = _renderTarget!.CreateBitmap(
                    new SizeI(cropW, cropH),
                    (IntPtr)srcPtr,
                    (uint)srcStride,
                    bmpProps
                );

                _renderTarget.DrawBitmap(
                    bmp,
                    new Vortice.RawRectF(destX, destY, destX + destW, destY + destH),
                    1.0f,
                    BitmapInterpolationMode.Linear,
                    null
                );
            }
            else
            {
                // Aspect-ratio fit
                float escala = Math.Min(destW / srcWidth, destH / srcHeight);
                float novoW = srcWidth * escala;
                float novoH = srcHeight * escala;
                float offsetX = destX + (destW - novoW) / 2f;
                float offsetY = destY + (destH - novoH) / 2f;

                using var bmp = _renderTarget!.CreateBitmap(
                    new SizeI(srcWidth, srcHeight),
                    dadosBgra,
                    (uint)srcStride,
                    bmpProps
                );

                _renderTarget.DrawBitmap(
                    bmp,
                    new Vortice.RawRectF(offsetX, offsetY, offsetX + novoW, offsetY + novoH),
                    1.0f,
                    BitmapInterpolationMode.Linear,
                    null
                );
            }
        }
        catch (Exception ex)
        {
            if (AppConfig.HabilitarLogsDiagnostico)
            {
                Console.WriteLine($"[GPU-Compositor] Erro ao desenhar frame: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Desenha um texto com a fonte Anton (ou fallback) na posição especificada.
    /// </summary>
    public void DesenharTexto(string texto, int tamanhoFonte, float x, float y, float maxWidth, float maxHeight,
                              byte r, byte g, byte b, byte a = 255)
    {
        if (string.IsNullOrEmpty(texto) || _renderTarget == null || _dwriteFactory == null) return;

        var textFormat = ObterTextFormat(tamanhoFonte);
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));

        _renderTarget.DrawText(
            texto,
            textFormat,
            new Vortice.RawRectF(x, y, x + maxWidth, y + maxHeight),
            brush
        );
    }

    /// <summary>
    /// Desenha um retângulo preenchido.
    /// </summary>
    public void DesenharRetangulo(float x, float y, float w, float h, byte r, byte g, byte b, byte a = 255)
    {
        if (_renderTarget == null) return;
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));
        _renderTarget.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), brush);
    }

    /// <summary>
    /// Desenha a borda de um retângulo (sem preenchimento).
    /// </summary>
    public void DesenharRetanguloBorda(float x, float y, float w, float h, byte r, byte g, byte b, byte a = 255, float espessura = 1f)
    {
        if (_renderTarget == null) return;
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));
        _renderTarget.DrawRectangle(new Vortice.RawRectF(x, y, x + w, y + h), brush, espessura);
    }

    /// <summary>
    /// Desenha uma linha.
    /// </summary>
    public void DesenharLinha(float x1, float y1, float x2, float y2, byte r, byte g, byte b, byte a = 255, float espessura = 1f)
    {
        if (_renderTarget == null) return;
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));
        _renderTarget.DrawLine(new System.Numerics.Vector2(x1, y1), new System.Numerics.Vector2(x2, y2), brush, espessura);
    }

    /// <summary>
    /// Desenha um círculo preenchido.
    /// </summary>
    public void DesenharCirculo(float cx, float cy, float raio, byte r, byte g, byte b, byte a = 255)
    {
        if (_renderTarget == null) return;
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));
        _renderTarget.FillEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy), raio, raio), brush);
    }

    /// <summary>
    /// Desenha a borda de um círculo (sem preenchimento).
    /// </summary>
    public void DesenharCirculoBorda(float cx, float cy, float raio, byte r, byte g, byte b, byte a = 255, float espessura = 1f)
    {
        if (_renderTarget == null) return;
        using var brush = _renderTarget.CreateSolidColorBrush(new Color4(r / 255f, g / 255f, b / 255f, a / 255f));
        _renderTarget.DrawEllipse(new Ellipse(new System.Numerics.Vector2(cx, cy), raio, raio), brush, espessura);
    }

    /// <summary>
    /// Desenha o GC (Lower-Third) estilo broadcast com fundo semitransparente.
    /// </summary>
    public void DesenharGC(float offsetX, float offsetY, float novoW, float novoH, string texto, int tamanhoFonte)
    {
        if (string.IsNullOrEmpty(texto) || _renderTarget == null || _dwriteFactory == null) return;

        var textFormat = ObterTextFormat(tamanhoFonte);

        using var textLayout = _dwriteFactory.CreateTextLayout(texto, textFormat, novoW, novoH);
        var metrics = textLayout.Metrics;
        float tw = metrics.Width;
        float th = metrics.Height;

        int paddingX = 18;
        int paddingV = 10;
        float alturaBarra = th + paddingV * 2;

        float gcX1 = offsetX;
        float gcY1 = offsetY + novoH - alturaBarra;
        float barW = Math.Min(tw + 2 * paddingX, novoW);
        float barH = alturaBarra;

        if (barW <= 0 || barH <= 0 || gcY1 < 0 || gcY1 + barH > Height || gcX1 < 0 || gcX1 + barW > Width)
            return;

        // Fundo semitransparente (RGB: 84, 0, 4 = vermelho escuro do original)
        using var brushFundo = _renderTarget.CreateSolidColorBrush(new Color4(84f / 255f, 0f, 4f / 255f, 0.85f));
        _renderTarget.FillRectangle(new Vortice.RawRectF(gcX1, gcY1, gcX1 + barW, gcY1 + barH), brushFundo);

        // Texto branco
        using var brushTexto = _renderTarget.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        _renderTarget.DrawText(
            texto,
            textFormat,
            new Vortice.RawRectF(gcX1 + paddingX, gcY1 + paddingV, gcX1 + barW, gcY1 + barH),
            brushTexto
        );
    }

    /// <summary>
    /// Finaliza a renderização do frame e faz flush do Direct2D.
    /// </summary>
    public void FinalizarFrame()
    {
        _renderTarget!.EndDraw();
    }

    /// <summary>
    /// Copia a textura da GPU para a CPU e retorna o ponteiro dos dados.
    /// </summary>
    public unsafe IntPtr DownloadFrame(out int stride)
    {
        stride = 0;
        if (_context == null || _canvasTexture == null || _stagingTexture == null)
            return IntPtr.Zero;

        _context.CopyResource(_stagingTexture, _canvasTexture);

        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        stride = (int)mapped.RowPitch;
        return mapped.DataPointer;
    }

    /// <summary>
    /// Desmapeia a textura de staging após o download.
    /// </summary>
    public void LiberarDownload()
    {
        _context?.Unmap(_stagingTexture!, 0);
    }

    private DWrite.IDWriteTextFormat ObterTextFormat(int tamanho)
    {
        if (_textFormatCache.TryGetValue(tamanho, out var cached))
            return cached;

        var format = _dwriteFactory!.CreateTextFormat(
            _customFontFamilyName,
            DWrite.FontWeight.Regular,
            DWrite.FontStyle.Normal,
            DWrite.FontStretch.Normal,
            tamanho
        );

        format.TextAlignment = DWrite.TextAlignment.Leading;
        format.ParagraphAlignment = DWrite.ParagraphAlignment.Near;

        _textFormatCache[tamanho] = format;
        return format;
    }

    /// <summary>
    /// Mede o tamanho do texto renderizado com o TextFormat especificado.
    /// </summary>
    public (float Width, float Height) MedirTexto(string texto, int tamanhoFonte, float maxWidth = 10000f)
    {
        if (_dwriteFactory == null) return (0, 0);
        var textFormat = ObterTextFormat(tamanhoFonte);
        using var layout = _dwriteFactory.CreateTextLayout(texto, textFormat, maxWidth, 10000f);
        var m = layout.Metrics;
        return (m.Width, m.Height);
    }

    public void Dispose()
    {
        foreach (var tf in _textFormatCache.Values)
        {
            try { tf.Dispose(); } catch { }
        }
        _textFormatCache.Clear();

        _dwriteFactory?.Dispose();
        _renderTarget?.Dispose();
        _d2dFactory?.Dispose();
        _stagingTexture?.Dispose();
        _canvasTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        _dwriteFactory = null;
        _renderTarget = null;
        _d2dFactory = null;
        _stagingTexture = null;
        _canvasTexture = null;
        _context = null;
        _device = null;
        Inicializado = false;
    }
}
