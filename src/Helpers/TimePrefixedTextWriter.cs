using System.Text;

public class TimePrefixedTextWriter : TextWriter
{
    private readonly TextWriter _originalOut;
    private bool _needsPrefix = true;
    private readonly StringBuilder _bufferAtivo = new StringBuilder();
    private readonly object _lockBuffer = new object();

    public TimePrefixedTextWriter(TextWriter originalOut)
    {
        _originalOut = originalOut;
    }

    public override Encoding Encoding => _originalOut.Encoding;

    private void EnfileirarNoDashboard(string texto)
    {
        lock (_lockBuffer)
        {
            _bufferAtivo.Append(texto);
            string conteudo = _bufferAtivo.ToString();
            
            int indexQuebra;
            while ((indexQuebra = conteudo.IndexOf('\n')) >= 0)
            {
                string linha = conteudo.Substring(0, indexQuebra).TrimEnd('\r');
                if (!string.IsNullOrEmpty(linha))
                {
                    LogManager.Escrever($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {linha}");
                }
                conteudo = conteudo.Substring(indexQuebra + 1);
            }
            
            _bufferAtivo.Clear();
            _bufferAtivo.Append(conteudo);
        }
    }

    public override void Write(char value)
    {
        EnfileirarNoDashboard(value.ToString());

        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(value);
        if (value == '\n')
        {
            _needsPrefix = true;
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        EnfileirarNoDashboard(value);

        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(value);
        if (value.EndsWith("\n") || value.EndsWith("\r"))
        {
            _needsPrefix = true;
        }
    }

    public override void WriteLine(string? value)
    {
        EnfileirarNoDashboard((value ?? "") + "\n");

        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
        }
        _originalOut.WriteLine(value);
        _needsPrefix = true;
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        if (buffer == null || count == 0) return;

        EnfileirarNoDashboard(new string(buffer, index, count));

        if (_needsPrefix)
        {
            _originalOut.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ");
            _needsPrefix = false;
        }
        _originalOut.Write(buffer, index, count);
        if (buffer[index + count - 1] == '\n' || buffer[index + count - 1] == '\r')
        {
            _needsPrefix = true;
        }
    }

    public override void WriteLine()
    {
        EnfileirarNoDashboard("\n");

        _originalOut.WriteLine();
        _needsPrefix = true;
    }
}
