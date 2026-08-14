using System.Text;

namespace BarcodePrinter.Labels.Zpl;

/// <summary>One parsed ZPL command. <see cref="Data"/> is populated only for
/// field-data commands (^FD/^FV), whose payload runs to the next ^FS.</summary>
public sealed record ZplCommand(
    char Control,
    string Name,
    string Parameters,
    string? Data,
    int StartIndex,
    int Length)
{
    public override string ToString() =>
        Data is null ? $"{Control}{Name}{Parameters}" : $"{Control}{Name}{Data}";
}

/// <summary>
/// Minimal ZPL II tokenizer — enough to find field data and rewrite it, which
/// is all the template engine needs (blueprint §6.2). It deliberately does NOT
/// interpret geometry: the client's file owns layout, we only bind values.
/// </summary>
public sealed class ZplDocument
{
    private ZplDocument(string source, IReadOnlyList<ZplCommand> commands)
    {
        Source = source;
        Commands = commands;
    }

    public string Source { get; }
    public IReadOnlyList<ZplCommand> Commands { get; }

    /// <summary>Commands whose payload is free-form data terminated by ^FS
    /// rather than comma-separated parameters.</summary>
    private static readonly HashSet<string> DataCommands = ["FD", "FV", "FX"];

    public static ZplDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var commands = new List<ZplCommand>();
        var i = 0;
        while (i < source.Length)
        {
            var control = source[i];
            if (control is not ('^' or '~'))
            {
                i++;   // whitespace/newlines between commands
                continue;
            }

            var start = i;
            i++;   // consume control char

            // Command names are two characters, except ^A@ and the ^A<font>
            // shorthand — both start with 'A' and are handled by the generic
            // "read two chars" rule plus parameter scanning.
            var nameLength = Math.Min(2, source.Length - i);
            if (nameLength <= 0)
            {
                break;
            }
            var name = source.Substring(i, nameLength).ToUpperInvariant();
            i += nameLength;

            if (DataCommands.Contains(name))
            {
                // Payload runs to the next ^FS (or end of input if malformed).
                var fsIndex = IndexOfFieldSeparator(source, i);
                var data = fsIndex < 0 ? source[i..] : source[i..fsIndex];
                var end = fsIndex < 0 ? source.Length : fsIndex;
                commands.Add(new ZplCommand(control, name, string.Empty, data, start, end - start));
                i = end;
                continue;
            }

            // Parameters run to the next control character.
            var paramEnd = i;
            while (paramEnd < source.Length && source[paramEnd] is not ('^' or '~'))
            {
                paramEnd++;
            }
            var parameters = source[i..paramEnd].Trim('\r', '\n');
            commands.Add(new ZplCommand(control, name, parameters, null, start, paramEnd - start));
            i = paramEnd;
        }

        return new ZplDocument(source, commands);
    }

    /// <summary>Finds the terminating ^FS, tolerating a literal '^' inside the
    /// payload only when it is not the start of ^FS.</summary>
    private static int IndexOfFieldSeparator(string source, int from)
    {
        for (var i = from; i < source.Length - 1; i++)
        {
            if (source[i] == '^' &&
                char.ToUpperInvariant(source[i + 1]) == 'F' &&
                i + 2 < source.Length &&
                char.ToUpperInvariant(source[i + 2]) == 'S')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Rewrites the payload of the ^FD at <paramref name="commandIndex"/>,
    /// preserving every other byte of the original file exactly (A-15: the
    /// client's geometry is transmitted unmodified).</summary>
    public string ReplaceData(IReadOnlyDictionary<int, string> replacements)
    {
        var sb = new StringBuilder(Source.Length + 64);
        var cursor = 0;
        for (var index = 0; index < Commands.Count; index++)
        {
            if (!replacements.TryGetValue(index, out var replacement))
            {
                continue;
            }
            var command = Commands[index];
            sb.Append(Source, cursor, command.StartIndex - cursor);
            sb.Append(replacement);
            cursor = command.StartIndex + command.Length;
        }
        sb.Append(Source, cursor, Source.Length - cursor);
        return sb.ToString();
    }
}
