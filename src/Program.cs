using Microsoft.AspNetCore.Http;
using NewTek;
using NewTek.NDI;

// ===========================================================================
// PONTO DE ENTRADA PRINCIPAL (BOOTSTRAP ENXUTO)
// ===========================================================================
class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.SetOut(new TimePrefixedTextWriter(Console.Out));
        AppConfig.CarregarConfiguracoes();

        if (!NDIlib.initialize())
        {
            Console.WriteLine("[!] Erro crítico: Falha ao inicializar a NDI SDK.");
            return;
        }

        NdiScanner.Iniciar();

        // Inicializa o motor de composição de vídeo (GPU DirectX 11 ou CPU OpenCV)
        VideoEngineManager.IniciarMotorConfigurado();

        AppConfig.MixerGlobal.Iniciar();
        SseManager.IniciarEnvioVu();
        SseManager.IniciarEnvioMetrics();

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
            var caminho = CaminhoHelper.ObterCaminhoFisico(Path.Combine("web", "static", "css", "comum.css"));
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
            var caminho = CaminhoHelper.ObterCaminhoFisico(Path.Combine("web", "static", "js", "comum.js"));
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
            var caminho = CaminhoHelper.ObterCaminhoFisico(Path.Combine("web", "templates", "painel.html"));
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
            var caminho = CaminhoHelper.ObterCaminhoFisico(Path.Combine("web", "templates", "dock.html"));
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

        // Registra rotas da API por módulo
        app.MapFontesRoutes();
        app.MapGravacaoRoutes();
        app.MapConfigRoutes();
        app.MapSseRoutes();

        // Roda a aplicação web em segundo plano
        Console.WriteLine("[*] Servidor web iniciado na porta 8634...");
        app.Start();

        // Inicializa o Painel de Controle Gráfico Nativo
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.Run(new PainelControleForm(app));

        // Cleanup
        NdiScanner.Parar();

        if (AppConfig.MotorVideo == "gpu")
        {
            VideoEngineGpu.Parar();
        }
        else
        {
            VideoEngine.Parar();
        }

        AppConfig.MixerGlobal.Parar();
        SseManager.PararEnvioVu();
        
        lock (AppConfig.LockFontes)
        {
            foreach (var rec in AppConfig.ReceptoresAtivos.Values)
            {
                rec.Parar();
            }
            AppConfig.ReceptoresAtivos.Clear();

            foreach (var rec in AppConfig.ReceptoresPreview.Values)
            {
                rec.Parar();
            }
            AppConfig.ReceptoresPreview.Clear();
        }
    }
}
