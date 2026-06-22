// ===========================================================================
// HELPER DE RESOLUÇÃO DE CAMINHOS DE ARQUIVOS
// ===========================================================================
public static class CaminhoHelper
{
    public static string? ObterCaminhoFisico(string subcaminho)
    {
        var caminhos = new[]
        {
            Path.Combine(AppContext.BaseDirectory, subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", subcaminho),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", subcaminho),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", subcaminho),
            subcaminho
        };

        foreach (var caminho in caminhos)
        {
            if (File.Exists(caminho)) return caminho;
        }
        return null;
    }
}
