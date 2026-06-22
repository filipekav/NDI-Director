using Microsoft.AspNetCore.Http;

// ===========================================================================
// ROTAS SSE (SERVER-SENT EVENTS)
// ===========================================================================
public static class SseRoutes
{
    public static void MapSseRoutes(this WebApplication app)
    {
        // SSE: Stream de Eventos em Tempo Real para o Painel Web
        app.MapGet("/api/eventos", async (HttpContext context) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            SseManager.AdicionarCliente(context.Response);

            await context.Response.WriteAsync("data: update\n\n");
            await context.Response.Body.FlushAsync();

            var tcs = new TaskCompletionSource<bool>();
            context.RequestAborted.Register(() => {
                SseManager.RemoverCliente(context.Response);
                tcs.TrySetResult(true);
            });

            while (!context.RequestAborted.IsCancellationRequested)
            {
                await Task.Delay(25000);
                try
                {
                    await context.Response.WriteAsync(": heartbeat\n\n");
                    await context.Response.Body.FlushAsync();
                }
                catch
                {
                    break;
                }
            }

            await tcs.Task;
        });
    }
}
