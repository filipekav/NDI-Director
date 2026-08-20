using Microsoft.AspNetCore.Http;

// ===========================================================================
// ROTAS DE CONFIGURAÇÕES GERAIS
// ===========================================================================
public static class ConfigRoutes
{
    public static void MapConfigRoutes(this WebApplication app)
    {
        // API: Obter configuracoes gerais
        app.MapGet("/api/configuracoes", () =>
        {
            return Results.Json(new
            {
                formatoAudio = AppConfig.FormatoAudioAtual,
                corFundo = AppConfig.CorFundoAtual,
                apagarTemporarios = AppConfig.ApagarTemporarios,
                qualidadeGravacao = AppConfig.QualidadeGravacao,
                habilitarLivePreview = AppConfig.HabilitarLivePreview,
                habilitarLogsDiagnostico = AppConfig.HabilitarLogsDiagnostico,
                mosaicoVertical = AppConfig.MosaicoVertical,
                paddingMosaico = AppConfig.PaddingMosaico,
                canvasLarguraHorizontal = AppConfig.CanvasLarguraHorizontal,
                canvasAlturaHorizontal = AppConfig.CanvasAlturaHorizontal,
                canvasLarguraVertical = AppConfig.CanvasLarguraVertical,
                canvasAlturaVertical = AppConfig.CanvasAlturaVertical,
                limiteSessoesNvenc = AppConfig.LimiteSessoesNvenc,
                motorVideo = AppConfig.MotorVideo,
                autoLipSync = AppConfig.AutoLipSync,
                atrasoAudioManualMs = AppConfig.AtrasoAudioManualMs,
                latenciaVideoMedidaMs = AppConfig.LatenciaVideoMedidaMs,
                atrasoAudioEfetivoMs = AppConfig.ObterAtrasoAudioEfetivoMs()
            });
        });

        // API: Definir resolução horizontal
        app.MapPost("/api/configuracoes/definir_resolucao_horizontal/{w}/{h}", (int w, int h) =>
        {
            if (w < 128 || w > 3840 || h < 128 || h > 2160)
            {
                return Results.BadRequest(new { status = "error", message = "Dimensoes invalidas (Largura: 128-3840, Altura: 128-2160)." });
            }
            AppConfig.CanvasLarguraHorizontal = w;
            AppConfig.CanvasAlturaHorizontal = h;
            Console.WriteLine($"[*] Dimensoes do mosaico horizontal alteradas para: {w}x{h}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Resolução do mosaico horizontal alterada para {w}x{h}.", "normal");
            return Results.Json(new { status = "ok", w, h });
        });

        // API: Definir resolução vertical
        app.MapPost("/api/configuracoes/definir_resolucao_vertical/{w}/{h}", (int w, int h) =>
        {
            if (w < 128 || w > 3840 || h < 128 || h > 2160)
            {
                return Results.BadRequest(new { status = "error", message = "Dimensoes invalidas (Largura: 128-3840, Altura: 128-2160)." });
            }
            AppConfig.CanvasLarguraVertical = w;
            AppConfig.CanvasAlturaVertical = h;
            Console.WriteLine($"[*] Dimensoes do mosaico vertical alteradas para: {w}x{h}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Resolução do mosaico vertical alterada para {w}x{h}.", "normal");
            return Results.Json(new { status = "ok", w, h });
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
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Espaçamento (padding) do mosaico definido para {valor}px.", "normal");
            return Results.Json(new { status = "ok", paddingMosaico = valor });
        });

        // API: Definir o limite de sessões NVENC (1-100)
        app.MapPost("/api/configuracoes/definir_limite_nvenc/{valor}", (int valor) =>
        {
            if (valor < 1 || valor > 100)
            {
                return Results.BadRequest(new { status = "error", message = "Limite NVENC inválido. Deve ser entre 1 e 100." });
            }
            AppConfig.LimiteSessoesNvenc = valor;
            Console.WriteLine($"[*] Limite de sessões NVENC definido para: {valor}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Limite de sessões NVENC alterado para {valor}.", "normal");
            return Results.Json(new { status = "ok", limiteSessoesNvenc = valor });
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
                AppConfig.SalvarConfiguracoes();
            }

            SseManager.NotificarClientes();
            return Results.Json(new { status = "ok" });
        });

        // API: Definir se o mosaico principal corta câmeras na vertical
        app.MapPost("/api/configuracoes/definir_mosaico_vertical/{valor}", (bool valor) =>
        {
            AppConfig.MosaicoVertical = valor;
            Console.WriteLine($"[*] Mosaico vertical alterado para: {valor}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Layout do mosaico alterado para {(valor ? "Vertical" : "Padrão")}.", "normal");
            return Results.Json(new { status = "ok", mosaicoVertical = valor });
        });

        // API: Definir formato de audio global (pcm / aac)
        app.MapPost("/api/configuracoes/definir_audio/{formato}", (string formato) =>
        {
            if (formato == "pcm" || formato == "aac")
            {
                AppConfig.FormatoAudioAtual = formato;
                Console.WriteLine($"[*] Formato de audio global alterado para: {formato}");
                AppConfig.SalvarConfiguracoes();
                SseManager.NotificarClientes();
                SseManager.LogAtividade($"Formato de áudio global alterado para '{formato.ToUpper()}'.", "normal");
                return Results.Json(new { status = "ok", formato = formato });
            }
            return Results.BadRequest(new { status = "error", message = "Formato invalido. Escolha 'pcm' ou 'aac'." });
        });

        // API: Definir se apaga arquivos temporarios
        app.MapPost("/api/configuracoes/definir_temporarios/{valor}", (bool valor) =>
        {
            AppConfig.ApagarTemporarios = valor;
            Console.WriteLine($"[*] Apagar arquivos temporários alterado para: {valor}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Configuração de apagar temporários {(valor ? "ativada" : "desativada")}.", "normal");
            return Results.Json(new { status = "ok", apagarTemporarios = valor });
        });

        // API: Definir se habilita logs de diagnostico de sincronia
        app.MapPost("/api/configuracoes/definir_diagnostico/{valor}", (bool valor) =>
        {
            AppConfig.HabilitarLogsDiagnostico = valor;
            Console.WriteLine($"[*] Exibição de logs de diagnóstico alterada para: {valor}");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Logs de diagnóstico no console {(valor ? "habilitados" : "desabilitados")}.", "normal");
            return Results.Json(new { status = "ok", habilitarLogsDiagnostico = valor });
        });

        // API: Definir se habilita previews ao vivo (Live Preview)
        app.MapPost("/api/configuracoes/definir_live_preview/{valor}", (bool valor) =>
        {
            AppConfig.HabilitarLivePreview = valor;
            Console.WriteLine($"[*] Habilitar Live Preview alterado para: {valor}");
            
            // Se foi desativado, limpa imediatamente os previews rodando em background
            if (!valor)
            {
                lock (AppConfig.LockFontes)
                {
                    foreach (var rec in AppConfig.ReceptoresPreview.Values)
                    {
                        Task.Run(() => rec.Parar());
                    }
                    AppConfig.ReceptoresPreview.Clear();
                }
            }
            
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Pre-visualizações em tempo real (Live Preview) {(valor ? "habilitadas" : "desabilitadas")}.", "normal");
            return Results.Json(new { status = "ok", habilitarLivePreview = valor });
        });

        // API: Definir qualidade de gravação global (alta / media / baixa)
        app.MapPost("/api/configuracoes/definir_qualidade/{qualidade}", (string qualidade) =>
        {
            if (qualidade == "alta" || qualidade == "media" || qualidade == "baixa")
            {
                AppConfig.QualidadeGravacao = qualidade;
                Console.WriteLine($"[*] Qualidade de gravação global alterada para: {qualidade}");
                AppConfig.SalvarConfiguracoes();
                SseManager.NotificarClientes();
                SseManager.LogAtividade($"Qualidade de gravação global alterada para '{qualidade}'.", "normal");
                return Results.Json(new { status = "ok", qualidade = qualidade });
            }
            return Results.BadRequest(new { status = "error", message = "Qualidade invalida. Escolha 'alta', 'media' ou 'baixa'." });
        });

        // API: Definir motor de vídeo (cpu / gpu) com troca a quente imediata
        app.MapPost("/api/configuracoes/definir_motor_video/{valor}", (string valor) =>
        {
            if (valor == "cpu" || valor == "gpu")
            {
                Task.Run(() => VideoEngineManager.ReiniciarMotor(valor));
                return Results.Json(new { status = "ok", motorVideo = valor });
            }
            return Results.BadRequest(new { status = "error", message = "Motor inválido. Escolha 'cpu' ou 'gpu'." });
        });

        // API: Reiniciar motor de vídeo atual
        app.MapPost("/api/configuracoes/reiniciar_motor_video", () =>
        {
            Task.Run(() => VideoEngineManager.ReiniciarMotor());
            return Results.Json(new { status = "ok", motorVideo = AppConfig.MotorVideo });
        });

        // API: Definir Lip-Sync (auto e offset manual)
        app.MapPost("/api/configuracoes/definir_lipsync/{auto}/{offset}", (bool auto, int offset) =>
        {
            if (offset < -500 || offset > 500)
            {
                return Results.BadRequest(new { status = "error", message = "Offset de Lip-Sync inválido. Deve ser entre -500ms e +500ms." });
            }

            AppConfig.AutoLipSync = auto;
            AppConfig.AtrasoAudioManualMs = offset;

            Console.WriteLine($"[*] Lip-Sync alterado: Auto={auto}, Offset={offset}ms, Efetivo={AppConfig.ObterAtrasoAudioEfetivoMs()}ms");
            AppConfig.SalvarConfiguracoes();
            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Lip-Sync alterado: {(auto ? "Automático" : "Manual")} (Atraso efetivo: {AppConfig.ObterAtrasoAudioEfetivoMs()}ms).", "normal");

            return Results.Json(new
            {
                status = "ok",
                autoLipSync = AppConfig.AutoLipSync,
                atrasoAudioManualMs = AppConfig.AtrasoAudioManualMs,
                latenciaVideoMedidaMs = AppConfig.LatenciaVideoMedidaMs,
                atrasoAudioEfetivoMs = AppConfig.ObterAtrasoAudioEfetivoMs()
            });
        });
    }
}
