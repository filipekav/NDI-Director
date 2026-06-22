// ===========================================================================
// ROTAS DE GRAVAÇÃO INDIVIDUAL (FFmpeg + NVENC)
// ===========================================================================
public static class GravacaoRoutes
{
    public static void MapGravacaoRoutes(this WebApplication app)
    {
        // API: Iniciar Gravação Individual via FFmpeg acelerado por NVIDIA GPU (NVENC)
        app.MapPost("/api/gravar/iniciar", (string nome) => IniciarGravar(nome));
        app.MapPost("/api/gravar/iniciar/{*nome}", (string nome) => IniciarGravar(nome));

        // API: Parar Gravação Individual
        app.MapPost("/api/gravar/parar", (string nome) => PararGravar(nome));
        app.MapPost("/api/gravar/parar/{*nome}", (string nome) => PararGravar(nome));
    }

    private static IResult IniciarGravar(string nome)
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

        if (!FonteHelper.GarantirReceptorConectado(nome))
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
        SseManager.LogAtividade($"Gravação individual iniciada para '{nome}'", "sucesso");
        return Results.Json(new { status = "ok", arquivo = caminhoArquivo });
    }

    private static IResult PararGravar(string nome)
    {
        if (string.IsNullOrEmpty(nome))
        {
            return Results.BadRequest(new { status = "error", message = "O nome da fonte nao foi especificado." });
        }

        GravadorFFmpeg? gravador = null;

        AppConfig.GravadoresAtivos.TryRemove(nome, out gravador);

        if (gravador != null)
        {
            Task.Run(() => gravador.Parar());

            ReceptorNDI? recParaParar = null;
            lock (AppConfig.LockFontes)
            {
                bool estaNaCena = Array.IndexOf(AppConfig.OrdemReceptores, nome) != -1;
                if (!estaNaCena)
                {
                    AppConfig.ReceptoresAtivos.TryRemove(nome, out recParaParar);
                }
            }

            if (recParaParar != null)
            {
                Task.Run(() => recParaParar.Parar());
            }

            SseManager.NotificarClientes();
            SseManager.LogAtividade($"Gravação individual interrompida para '{nome}'", "normal");
            return Results.Json(new { status = "ok" });
        }

        return Results.BadRequest(new { status = "error", message = "Nao ha gravacao ativa para este participante." });
    }
}
