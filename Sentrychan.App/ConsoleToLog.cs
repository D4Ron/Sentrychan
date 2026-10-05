using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sentrychan.App;

/// <summary>
/// Many parts of the app report with Console.WriteLine, which goes nowhere in a windowed app. In
/// test builds the console is pointed at the log, so a tester's log has those lines too.
/// </summary>
internal sealed class ConsoleToLog(ILogger log) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_line)
        {
            if (value == '\n') Flush();
            else if (value != '\r') _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null) return;
        foreach (var c in value) Write(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        lock (_line)
        {
            if (_line.Length == 0) return;
            var text = _line.ToString();
            _line.Clear();
            var failed = text.Contains("fail", StringComparison.OrdinalIgnoreCase) || text.Contains("exception", StringComparison.OrdinalIgnoreCase)
                         || text.Contains("error", StringComparison.OrdinalIgnoreCase);
            log.Log(failed ? LogLevel.Warning : LogLevel.Information, "[Console] {Line}", text);
        }
    }
}
