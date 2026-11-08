using System.Text;
using System.Text.RegularExpressions;

namespace Sonqelra.Lang;

public sealed record Position(int Line, int Column);

public sealed record Span(string Filename, Position Start, Position End);

public class SonqelraException(string message, Span span, Exception? inner = null)
    : Exception($"{span.Filename}:{span.Start.Line}:{span.Start.Column}: {message}", inner)
{
    public Span Span { get; } = span;
}

public sealed class LexException(string message, Span span) : SonqelraException(message, span);

public sealed class ParseException(string message, Span span) : SonqelraException(message, span);

public sealed class SemanticException(string message, Span span, Exception? inner = null)
    : SonqelraException(message, span, inner);

public sealed class ExportException(string message, Span span, Exception? inner = null)
    : SonqelraException(message, span, inner);

public sealed record Token(string Kind, string Value, Span Span);

public sealed record Node(string Kind, Span Span, object? Value = null, IReadOnlyList<Node>? Items = null)
{
    public IReadOnlyList<Node> Children => Items ?? Array.Empty<Node>();
}

public static class Lexer
{
    public static IReadOnlyList<Token> Lex(string source, string filename = "<string>")
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, col = 1, depth = 0;
        Position Pos() => new(line, col);

        void Emit(string kind, string value, Position start) =>
            tokens.Add(new(kind, value, new(filename, start, Pos())));

        while (i < source.Length)
        {
            var start = Pos();
            char c = source[i];
            if (c is ' ' or '\t' or '\r')
            {
                i++;
                col++;
                continue;
            }

            if (c == '\n')
            {
                i++;
                line++;
                col = 1;
                if (depth == 0) Emit("NL", "\n", start);
                continue;
            }

            if (source.AsSpan(i).StartsWith("//"))
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                    col++;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                col++;
                var value = new StringBuilder();
                while (i < source.Length && source[i] != quote)
                {
                    char ch = source[i];
                    if (ch == '\n') throw new LexException("Unterminated string", new(filename, start, Pos()));
                    if (ch == '\\')
                    {
                        i++;
                        col++;
                        if (i >= source.Length || !"\\\"'nrt".Contains(source[i]))
                            throw new LexException("Invalid string escape", new(filename, start, Pos()));
                        ch = source[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var x => x };
                    }

                    value.Append(ch);
                    i++;
                    col++;
                }

                if (i == source.Length) throw new LexException("Unterminated string", new(filename, start, Pos()));
                i++;
                col++;
                Emit("STRING", value.ToString(), start);
                continue;
            }

            var m = Regex.Match(source[i..], @"\A(?:[A-Za-z_][A-Za-z_0-9#]*(?:-\d+)?|-?\d+st|-?\d+)",
                RegexOptions.CultureInvariant);
            if (m.Success)
            {
                string word = m.Value;
                i += word.Length;
                col += word.Length;
                string kind = Regex.IsMatch(word, @"\A[A-G](?:#+|b+)?-?\d+\z") ? "PITCH" :
                    Regex.IsMatch(word, @"\A-?\d+st\z") ? "INTERVAL" :
                    Regex.IsMatch(word, @"\A-?\d+\z") ? "INT" :
                    Regex.IsMatch(word, @"\A(?:[A-Za-z_][A-Za-z_0-9]*|[A-G](?:#+|b+))\z") ? "IDENT" :
                    throw new LexException($"Invalid token '{word}'", new(filename, start, Pos()));
                Emit(kind, word, start);
                continue;
            }

            if (source.AsSpan(i).StartsWith("|>") || source.AsSpan(i).StartsWith("->"))
            {
                var op = source.Substring(i, 2);
                i += 2;
                col += 2;
                Emit(op, op, start);
                continue;
            }

            if ("{}()[],:@=+|/<>.".Contains(c))
            {
                i++;
                col++;
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth = Math.Max(0, depth - 1);
                Emit(c.ToString(), c.ToString(), start);
                continue;
            }

            throw new LexException($"Unexpected character '{c}'", new(filename, start, new(line, col + 1)));
        }

        var pos = Pos();
        tokens.Add(new("EOF", "", new(filename, pos, pos)));
        return tokens;
    }
}

public sealed class Parser(IReadOnlyList<Token> tokens)
{
    private int _i;
    private Token T => tokens[_i];

    private Token Take(string? value = null)
    {
        var t = T;
        if (value != null && t.Kind != value && t.Value != value)
            throw new ParseException($"Expected {value}, got {(t.Value == "" ? t.Kind : t.Value)}", t.Span);
        _i++;
        return t;
    }

    private bool Is(string value) => T.Kind == value || T.Value == value;

    private Node Make(string kind, Token start, object? value = null, IEnumerable<Node>? children = null) => new(kind,
        new(start.Span.Filename, start.Span.Start, tokens[_i - 1].Span.End), value, children?.ToArray());

    private void Nl()
    {
        while (Is("NL")) Take();
    }

    private void End()
    {
        if (Is("NL")) Nl();
        else if (!Is("}") && !Is("EOF")) throw new ParseException("Expected end of statement", T.Span);
    }

    private string Ident()
    {
        var t = Take("IDENT");
        return new[]
        {
            "score", "tempo", "meter", "key", "melody", "let", "fn", "return", "track", "instrument", "percussion",
            "play", "export", "import", "midi", "true", "false", "major", "minor", "rest"
        }.Contains(t.Value)
            ? throw new ParseException("Expected identifier", t.Span)
            : t.Value;
    }

    public static IReadOnlyList<Node> Parse(string source, string filename = "<string>") =>
        new Parser(Lexer.Lex(source, filename)).Program();

    public IReadOnlyList<Node> Program()
    {
        var items = new List<Node>();
        Nl();
        while (!Is("EOF"))
        {
            items.Add(Is("score") ? Score() : Is("fn") ? Function() :
                Is("import") ? Statement("top") : throw new ParseException("Expected score, fn or import", T.Span));
            End();
        }

        return items;
    }

    private Node Score()
    {
        var s = Take("score");
        var title = Take("STRING").Value;
        Take("{");
        Nl();
        var items = new List<Node>();
        while (!Is("}"))
        {
            if (Is("EOF")) throw new ParseException("Expected }", T.Span);
            items.Add(Statement("score"));
            End();
        }

        Take("}");
        return Make("score", s, title, items);
    }

    private Node Statement(string context)
    {
        var s = T;
        var k = s.Value;
        if (k == "fn" && context == "score") return Function();
        if (k == "let")
        {
            Take();
            var name = Ident();
            Take("=");
            return Make("let", s, name, [Expr()]);
        }

        if (k == "tempo" && context == "score")
        {
            Take();
            return Make(k, s, null, [Expr()]);
        }

        if (k == "meter" && context == "score")
        {
            Take();
            var a = int.Parse(Take("INT").Value);
            Take("/");
            var b = int.Parse(Take("INT").Value);
            return Make(k, s, (a, b));
        }

        if (k == "key" && context == "score")
        {
            Take();
            var root = Take("IDENT").Value;
            var mode = Take("IDENT");
            if (mode.Value is not ("major" or "minor")) throw new ParseException("Expected major or minor", mode.Span);
            return Make(k, s, (root, mode.Value));
        }

        if (k == "melody" && context == "score")
        {
            Take();
            var name = Ident();
            Take("{");
            Nl();
            var notes = new List<Node>();
            while (!Is("}"))
            {
                var t = T;
                if (!Is("PITCH") && !Is("rest")) throw new ParseException("Expected note or rest", t.Span);
                Take();
                Take(":");
                var a = System.Numerics.BigInteger.Parse(Take("INT").Value);
                Take("/");
                var b = System.Numerics.BigInteger.Parse(Take("INT").Value);
                int? velocity = null;
                if (Is("@"))
                {
                    Take();
                    velocity = int.Parse(Take("INT").Value);
                }

                notes.Add(Make("note", t, (t.Value, a, b, velocity)));
                End();
            }

            Take("}");
            return Make(k, s, name, notes);
        }

        if (k == "track" && context == "score")
        {
            Take();
            var name = Is("STRING") ? Take().Value : Ident();
            Take("{");
            Nl();
            var items = new List<Node>();
            while (!Is("}"))
            {
                items.Add(Statement("track"));
                End();
            }

            Take("}");
            return Make(k, s, name, items);
        }

        if ((k == "export" && context == "score") || (k == "import" && context is "score" or "top"))
        {
            Take();
            var format = Take("IDENT").Value;
            var path = Take("STRING").Value;
            return Make(k, s, (format, path));
        }

        if (k == "instrument" && context == "track")
        {
            Take();
            return Make(k, s, Take("STRING").Value);
        }

        if (k == "percussion" && context == "track")
        {
            Take();
            var t = Take("IDENT");
            if (t.Value is not ("true" or "false")) throw new ParseException("Expected true or false", t.Span);
            return Make(k, s, t.Value == "true");
        }

        if (k == "play" && context == "track")
        {
            Take();
            return Make(k, s, null, [Expr()]);
        }

        throw new ParseException($"Unexpected statement '{k}'", s.Span);
    }

    private string TypeName()
    {
        var t = Take("IDENT");
        if (t.Value == "List")
        {
            Take("<");
            Take("Pitch");
            Take(">");
            return "List<Pitch>";
        }

        if (t.Value is not ("Pitch" or "Music" or "Int" or "Number" or "Interval"))
            throw new ParseException("Unknown type", t.Span);
        return t.Value;
    }

    private Node Function()
    {
        var s = Take("fn");
        var name = Ident();
        Take("(");
        var nodes = new List<Node>();
        if (!Is(")"))
        {
            while (true)
            {
                var p = T;
                var n = Ident();
                Take(":");
                var ty = TypeName();
                nodes.Add(Make("param", p, (n, ty)));
                if (!Is(",")) break;
                Take();
            }
        }

        Take(")");
        Take("->");
        var result = TypeName();
        Take("{");
        Nl();
        while (Is("let"))
        {
            nodes.Add(Statement("fn"));
            End();
        }

        Take("return");
        nodes.Add(Expr());
        End();
        Take("}");
        return Make("fn", s, (name, result), nodes);
    }

    private Node Expr()
    {
        var left = Binary(0);
        while (true)
        {
            int save = _i;
            if (Is("NL"))
            {
                Nl();
                if (!Is("|>"))
                {
                    _i = save;
                    break;
                }
            }

            if (!Is("|>")) break;
            Take();
            var name = Ident();
            var args = Args();
            left = new Node("pipe", new Span(left.Span.Filename, left.Span.Start, tokens[_i - 1].Span.End), name,
                [left, .. args]);
        }

        return left;
    }

    private Node Binary(int level)
    {
        if (level == 2) return Primary();
        var left = Binary(level + 1);
        var op = level == 0 ? "|" : "+";
        while (Is(op))
        {
            Take();
            var right = Binary(level + 1);
            left = new Node(op, new Span(left.Span.Filename, left.Span.Start, right.Span.End), null, [left, right]);
        }

        return left;
    }

    private List<Node> Args()
    {
        Take("(");
        var args = new List<Node>();
        if (!Is(")"))
        {
            while (true)
            {
                var s = T;
                string? name = null;
                if (T.Kind == "IDENT" && tokens[_i + 1].Kind == ":")
                {
                    name = Take().Value;
                    Take(":");
                }

                var arg = Expr();
                args.Add(Make("arg", s, name, [arg]));
                if (!Is(",")) break;
                Take();
            }
        }

        Take(")");
        return args;
    }

    private Node Primary()
    {
        var s = T;
        if (Is("PITCH"))
        {
            Take();
            return Make("pitch", s, s.Value);
        }

        if (Is("INTERVAL"))
        {
            Take();
            return Make("interval", s, int.Parse(s.Value[..^2]));
        }

        if (Is("INT"))
        {
            Take();
            var a = System.Numerics.BigInteger.Parse(s.Value);
            System.Numerics.BigInteger b = 1;
            if (Is("/"))
            {
                Take();
                b = System.Numerics.BigInteger.Parse(Take("INT").Value);
            }

            return Make("number", s, (a, b));
        }

        if (Is("IDENT") && (s.Value is "module" or "score") && tokens[_i + 1].Kind == "(")
        {
            var basis = Take().Value;
            Take("("); string? module = Take("STRING").Value; Take(")");
            string title;
            if (basis == "module") { Take("."); title = Take("STRING").Value; }
            else { title = module; module = null; }
            Take("."); var category = Take("IDENT").Value;
            if (category is not ("fn" or "track")) throw new ParseException("Expected fn or track", T.Span);
            Take("."); var member = Take(category == "fn" ? "IDENT" : "STRING").Value;
            return Make("qualified", s, (module, title, category, member), category == "fn" ? Args() : null);
        }
        if (Is("IDENT"))
        {
            var name = Take().Value;
            return Is("(") ? Make("call", s, name, Args()) : Make("name", s, name);
        }

        if (Is("["))
        {
            Take();
            var items = new List<Node>();
            if (!Is("]"))
            {
                while (true)
                {
                    items.Add(Expr());
                    if (!Is(",")) break;
                    Take();
                    if (Is("]")) break;
                }
            }

            Take("]");
            return Make("list", s, null, items);
        }

        if (Is("("))
        {
            Take();
            var n = Expr();
            Take(")");
            return n;
        }

        throw new ParseException("Expected expression", s.Span);
    }
}