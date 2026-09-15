using System;
using System.IO;
using System.Text;

namespace DevKernel.Shell;

/// <summary>
/// Adds a small, command-scoped pager to the shell's existing console output.
/// Commands can continue using Console.Write/WriteLine; this writer pauses
/// after one screenful without changing the boot banner or input prompt.
/// </summary>
internal sealed class PagedTextWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly int _pageLines;
    private int _lines;
    private bool _stopOutput;

    public PagedTextWriter(TextWriter inner, int pageLines)
    {
        _inner = inner;
        _pageLines = pageLines < 4 ? 4 : pageLines;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        if (_stopOutput)
        {
            return;
        }

        _inner.Write(value);
        if (value == '\n')
        {
            _lines++;
            if (_lines >= _pageLines)
            {
                Pause();
            }
        }
    }

    public override void Write(string? value)
    {
        if (value == null || _stopOutput)
        {
            return;
        }

        for (int i = 0; i < value.Length; i++)
        {
            Write(value[i]);
            if (_stopOutput)
            {
                return;
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        if (_stopOutput)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            Write(buffer[index + i]);
            if (_stopOutput)
            {
                return;
            }
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        if (!_stopOutput)
        {
            Write('\n');
        }
    }

    public override void Flush() => _inner.Flush();

    private void Pause()
    {
        _inner.Write("\r\n-- More -- [Space/Enter: next, Q/Esc: stop] ");
        _inner.Flush();

        while (true)
        {
            ConsoleKey key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.Q || key == ConsoleKey.Escape)
            {
                _stopOutput = true;
                break;
            }

            if (key == ConsoleKey.Spacebar || key == ConsoleKey.Enter)
            {
                break;
            }
        }

        _inner.Write("\r\n");
        _inner.Flush();
        _lines = 0;
    }
}
