using System;

public static class LogManager
{
    public static event Action<string>? AoEscreverLog;

    public static void Escrever(string log)
    {
        try
        {
            AoEscreverLog?.Invoke(log);
        }
        catch
        {
            // Previne falha de concorrência ou ouvintes descartados
        }
    }
}
