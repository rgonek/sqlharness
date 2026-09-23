using System.Text;

namespace SqlHarness.Core;

/// <summary>
/// Parses a SQL Server missing-index column list and returns catalog spellings.
/// ]] inside brackets is one ]. Every invalid list throws a fixed message.
/// </summary>
internal static class BracketedIdentifierListParser
{
    private const string MalformedMessage = "Missing-index column metadata is malformed.";

    internal static IReadOnlyList<string> ParseAndResolve(
        string? text,
        IReadOnlyList<string> catalogColumns)
    {
        var parsed = Parse(text);
        return Resolve(parsed, catalogColumns);
    }

    private static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var names = new List<string>();
        var current = new StringBuilder();
        var state = State.BeforeIdentifier;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            switch (state)
            {
                case State.BeforeIdentifier:
                    if (char.IsWhiteSpace(character))
                        continue;

                    if (character == '[')
                    {
                        current.Clear();
                        state = State.InsideIdentifier;
                        continue;
                    }

                    throw Malformed();
                case State.AfterIdentifier:
                    if (char.IsWhiteSpace(character))
                        continue;

                    if (character == ',')
                    {
                        state = State.BeforeIdentifier;
                        continue;
                    }

                    if (character == '[')
                    {
                        current.Clear();
                        state = State.InsideIdentifier;
                        continue;
                    }

                    throw Malformed();
                case State.InsideIdentifier:
                    if (character != ']')
                    {
                        current.Append(character);
                        continue;
                    }

                    if (index + 1 < text.Length && text[index + 1] == ']')
                    {
                        current.Append(']');
                        index++;
                        continue;
                    }

                    if (current.Length == 0)
                        throw Malformed();

                    names.Add(current.ToString());
                    state = State.AfterIdentifier;
                    continue;
            }
        }

        if (state != State.AfterIdentifier)
            throw Malformed();

        return names;
    }

    private static List<string> Resolve(List<string> parsed, IReadOnlyList<string> catalogColumns)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>(parsed.Count);
        foreach (var name in parsed)
        {
            if (!seen.Add(name))
                throw Malformed();

            string? spelling = null;
            foreach (var column in catalogColumns)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(name, column))
                    continue;

                if (spelling is not null)
                    throw Malformed();

                spelling = column;
            }

            if (spelling is null)
                throw Malformed();

            resolved.Add(spelling);
        }

        return resolved;
    }

    private static InvalidOperationException Malformed() =>
        new(MalformedMessage);

    private enum State
    {
        BeforeIdentifier,
        InsideIdentifier,
        AfterIdentifier,
    }
}