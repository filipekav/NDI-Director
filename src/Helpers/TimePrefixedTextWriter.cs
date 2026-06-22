using System.Text;

public class TimePrefixedTextWriter : TextWriter
{
    private readonly TextWriter _originalOut;
    private bool _needsPrefix = true;

    public TimePrefixedTextWriter(TextWriter originalOut)
    {
        _originalOut = originalOut;
    }

    public override Encoding Encoding => _originalOut.Encoding;

    public override void Write(char value)
    {
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
        _originalOut.WriteLine();
        _needsPrefix = true;
    }
}
