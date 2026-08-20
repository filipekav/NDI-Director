using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenCvSharp;

// ===========================================================================
// ROTAS DE FONTES, TOGGLE, HIGHLIGHT, SOLO, POSIÇÃO E APELIDO
// ===========================================================================
public static class FontesRoutes
{
    public static void MapFontesRoutes(this WebApplication app)
    {
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
                        if (AppConfig.ReceptoresAtivos.TryRemove(nome, out var rec))
                        {
                            recParaParar = rec;
                        }
                        Console.WriteLine($"[-] Desconectado e removido da cena: {nome}");
                        SseManager.LogAtividade($"'{nome}' removido da cena.", "normal");
                    }
                    else
                    {
                        // Se estiver gravando, mantemos o ReceptorNDI ativo em segundo plano!
                        Console.WriteLine($"[-] Removido da cena mas mantido em background para gravacao: {nome}");
                        SseManager.LogAtividade($"'{nome}' removido da cena (mantido em background para gravação).", "normal");
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
                        SseManager.LogAtividade($"'{nome}' adicionado à cena.", "sucesso");
                    }
                    else
                    {
                        Console.WriteLine($"[+] Trazendo feed que ja estava gravando em background para a cena: {nome}");
                        SseManager.LogAtividade($"'{nome}' adicionado à cena (já ativo em background).", "sucesso");
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

        // API: Alternar modo Destaque (Highlight)
        app.MapPost("/api/highlight/{*nome}", (string nome) =>
        {
            if (!FonteHelper.GarantirFonteAtiva(nome))
            {
                return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos atingido." });
            }

            string logMsg = "";
            lock (AppConfig.LockFontes)
            {
                if (AppConfig.FonteHighlight == nome)
                {
                    AppConfig.FonteHighlight = null;
                    Console.WriteLine($"[*] Highlight desativado para: {nome}");
                    logMsg = $"Destaque desativado para '{nome}'.";
                }
                else
                {
                    AppConfig.FonteHighlight = nome;
                    Console.WriteLine($"[*] Highlight ativado para: {nome}");
                    logMsg = $"Destaque ativado para '{nome}'.";
                }
            }

            SseManager.NotificarClientes();
            SseManager.LogAtividade(logMsg, "normal");
            return Results.Json(new { status = "ok" });
        });

        // API: Alternar modo Solo
        app.MapPost("/api/solo/{*nome}", (string nome) =>
        {
            if (!FonteHelper.GarantirFonteAtiva(nome))
            {
                return Results.BadRequest(new { status = "limit_reached", message = "Limite maximo de 4 feeds ativos atingido." });
            }

            string logMsg = "";
            lock (AppConfig.LockFontes)
            {
                if (AppConfig.FonteSolo == nome)
                {
                    AppConfig.FonteSolo = null;
                    Console.WriteLine($"[*] Solo desativado para: {nome}");
                    logMsg = $"Modo Solo desativado para '{nome}'.";
                }
                else
                {
                    AppConfig.FonteSolo = nome;
                    AppConfig.FonteHighlight = null; // Solo cancela highlight
                    Console.WriteLine($"[*] Solo ativado para: {nome}");
                    logMsg = $"Modo Solo ativado para '{nome}'.";
                }
            }

            SseManager.NotificarClientes();
            SseManager.LogAtividade(logMsg, "normal");
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
                        SseManager.LogAtividade($"'{nome}' adicionado ao Mosaico na posição {novaPos + 1}.", "sucesso");
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
                        SseManager.LogAtividade($"'{nome}' movido para a posição {novaPos + 1} (anterior: {idxAtual + 1}).", "normal");
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
                    AppConfig.SalvarConfiguracoes();
                }

                SseManager.NotificarClientes();
                string logMsg = string.IsNullOrEmpty(apelido)
                    ? $"Apelido de GC removido para '{nome}'."
                    : $"Apelido de GC de '{nome}' definido para '{apelido}'.";
                SseManager.LogAtividade(logMsg, "normal");
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
                    AppConfig.SalvarConfiguracoes();
                    SseManager.NotificarClientes();
                    SseManager.LogAtividade($"Cor de fundo do mosaico alterada para '{cor}'.", "normal");
                    return Results.Json(new { status = "ok" });
                }
            }
            return Results.BadRequest(new { status = "error", message = "Cor invalida." });
        });

        // API: Capturar thumbnail estática (Preview Card)
        app.MapGet("/api/preview/{*nome}", (string nome) =>
        {
            ReceptorNDI? rec;
            lock (AppConfig.LockFontes)
            {
                if (!AppConfig.ReceptoresAtivos.TryGetValue(nome, out rec))
                {
                    AppConfig.ReceptoresPreview.TryGetValue(nome, out rec);
                }
            }

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
                    rec = new ReceptorNDI(nome);
                    AppConfig.ReceptoresPreview[nome] = rec;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Erro ao criar receptor de preview para '{nome}': {ex.Message}");
                    return Results.NoContent();
                }
            }

            Mat? frame = rec.ObterFrame();

            // Se o primeiro frame ainda não chegou, aguarda brevemente
            if (frame == null)
            {
                int tentativas = 12;
                while (tentativas-- > 0 && frame == null)
                {
                    Thread.Sleep(50);
                    frame = rec.ObterFrame();
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
    }
}

// ===========================================================================
// HELPERS DE FONTES (extraídos das funções locais do Main)
// ===========================================================================
public static class FonteHelper
{
    // Garantir que uma fonte esteja ativa na cena
    public static bool GarantirFonteAtiva(string nome)
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

    // Conectar o receptor na rede em background apenas (para gravação ou preview)
    public static bool GarantirReceptorConectado(string nome)
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
}
