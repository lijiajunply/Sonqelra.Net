using System.Numerics;
using System.Text.RegularExpressions;
using Sonqelra.Core;
using Sonqelra.Backends;

namespace Sonqelra.Lang;

public sealed record KeySignature(string Root, string Mode);

public sealed record ScoreResult(Score Score, KeySignature? Key)
{
    internal Dictionary<string, Function> Functions { get; init; } = [];
}

public sealed record ExportPlan(ScoreResult Score, string Path, Span Span, string Format);

public sealed record CompilationResult(IReadOnlyList<ScoreResult> Scores, IReadOnlyList<ExportPlan> Exports);

internal sealed record Interval(int Semitones);

internal sealed record Function(Node Node, Dictionary<string, object> Closure);

internal sealed record TypeValue(string Name);

public static class Compiler
{
    private static readonly HashSet<string> Builtins = ["note", "rest", "transpose", "repeat", "chord", "sequence"];
    private static SemanticException Fail(Node n, string message) => new(message, n.Span);

    private static string TypeOf(object v) => v switch
    {
        TypeValue t => t.Name, Music => "Music", Pitch => "Pitch", Interval => "Interval", Pitch[] => "List<Pitch>",
        BigInteger => "Int", Rational => "Number", _ => "unknown"
    };

    private static bool Accepts(string actual, string expected) =>
        actual == expected || (expected == "Number" && actual == "Int");

    private static object Require(Node n, object value, string expected)
    {
        var actual = TypeOf(value);
        if (!Accepts(actual, expected)) throw Fail(n, $"Expected {expected}, got {actual}");
        return value;
    }

    private static Rational Number(object value) => value switch
    {
        BigInteger i => new Rational(i), Rational r => r, _ => throw new InvalidOperationException()
    };

    private static int Int(object value) => (int)(BigInteger)value;

    private static void Bind(Dictionary<string, object> env, string name, object value, Node node)
    {
        if (Builtins.Contains(name)) throw Fail(node, $"Cannot shadow builtin {name}");
        if (!env.TryAdd(name, value)) throw Fail(node, $"Duplicate declaration {name}");
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
    {
        var env = new Dictionary<string, object>(a);
        foreach (var pair in b) env[pair.Key] = pair.Value;
        return env;
    }

    private static object Evaluate(Node n, Dictionary<string, object> env)
    {
        try
        {
            return Eval(n, env);
        }
        catch (SonqelraException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or ArithmeticException or OverflowException)
        {
            throw new SemanticException(ex.Message, n.Span, ex);
        }
    }

    private static object Eval(Node n, Dictionary<string, object> env)
    {
        switch (n.Kind)
        {
            case "number":
                var (a, b) = ((BigInteger, BigInteger))n.Value!;
                if (b <= 0) throw Fail(n, "Number denominator must be positive");
                return b == 1 ? (object)a : new Rational(a, b);
            case "pitch":
                var p = Pitch.Parse((string)n.Value!);
                _ = p.Midi;
                return p;
            case "interval": return new Interval((int)n.Value!);
            case "name":
                return env.TryGetValue((string)n.Value!, out var value)
                    ? value
                    : throw Fail(n, $"Unknown name {n.Value}");
            case "list":
                if (n.Children.Count == 0) throw Fail(n, "Pitch list must not be empty");
                return n.Children.Select(c => (Pitch)Require(c, Evaluate(c, env), "Pitch")).ToArray();
            case "+":
            case "|":
                var left = Evaluate(n.Children[0], env);
                var right = Evaluate(n.Children[1], env);
                if (n.Kind == "+" && left is Pitch pitch && right is Interval interval)
                    return pitch.Transpose(interval.Semitones);
                var x = (Music)Require(n, left, "Music");
                var y = (Music)Require(n, right, "Music");
                return n.Kind == "+" ? x + y : x | y;
            case "qualified":
                var qualified = ResolveQualified(n, env);
                if (((ValueTuple<string?, string, string, string>)n.Value!).Item3 == "track") return qualified;
                if (n.Children.Any(node => node.Value != null)) throw Fail(n, "Named arguments are not supported here");
                var qualifiedEnv = new Dictionary<string, object>(env) { ["__qualified__"] = qualified };
                return Call(n, "__qualified__", [.. n.Children.Select(node => Evaluate(node.Children[0], env))], [],
                    qualifiedEnv);
            case "call":
            case "pipe":
                var name = (string)n.Value!;
                var args = new List<object>();
                var named = new Dictionary<string, object>();
                bool seen = false;
                int start = 0;
                if (n.Kind == "pipe")
                {
                    if (name is not ("transpose" or "repeat"))
                        throw Fail(n, "Pipeline only supports transpose and repeat");
                    args.Add(Evaluate(n.Children[0], env));
                    start = 1;
                }

                foreach (var arg in n.Children.Skip(start))
                {
                    var v = Evaluate(arg.Children[0], env);
                    if (arg.Value is null)
                    {
                        if (seen) throw Fail(arg, "Positional argument follows named argument");
                        args.Add(v);
                    }
                    else
                    {
                        seen = true;
                        if (!named.TryAdd((string)arg.Value, v)) throw Fail(arg, $"Duplicate argument {arg.Value}");
                    }
                }

                return Call(n, name, args, named, env);
            default: throw Fail(n, $"Unsupported expression {n.Kind}");
        }
    }

    private static object Call(Node n, string name, List<object> args, Dictionary<string, object> named,
        Dictionary<string, object> env)
    {
        if (name is "chord" or "sequence")
        {
            if (args.Count != 1 || !named.ContainsKey("duration") ||
                named.Keys.Any(k => k is not ("duration" or "velocity")))
                throw Fail(n, $"{name} expects a pitch list and named duration, with optional velocity");
            var pitches = (Pitch[])Require(n, args[0], "List<Pitch>");
            var duration = Number(Require(n, named["duration"], "Number"));
            var velocity = named.TryGetValue("velocity", out var v) ? Int(Require(n, v, "Int")) : 80;
            if (duration <= 0 || velocity is < 0 or > 127) throw Fail(n, "Invalid duration or velocity");
            var notes = pitches.Select(Music (p) => new Note(p, duration * 4, velocity)).ToArray();
            return name == "chord" ? new Core.Parallel(notes) : new Sequence(notes);
        }

        if (named.Count != 0) throw Fail(n, "Named arguments are not supported here");
        if (Builtins.Contains(name))
        {
            if ((name == "note" && args.Count is not (2 or 3)) || (name == "rest" && args.Count != 1) ||
                ((name is "repeat" or "transpose") && args.Count != 2))
                throw Fail(n, $"Wrong number of arguments for {name}");
            if (name == "note")
            {
                var pitch = (Pitch)Require(n, args[0], "Pitch");
                var duration = Number(Require(n, args[1], "Number"));
                var velocity = args.Count == 3 ? Int(Require(n, args[2], "Int")) : 80;
                if (duration <= 0 || velocity is < 0 or > 127) throw Fail(n, "Invalid duration or velocity");
                return new Note(pitch, duration, velocity);
            }

            if (name == "rest")
            {
                var duration = Number(Require(n, args[0], "Number"));
                if (duration < 0) throw Fail(n, "Rest duration must be non-negative");
                return new Rest(duration);
            }

            var music = (Music)Require(n, args[0], "Music");
            int count = Int(Require(n, args[1], "Int"));
            if (name == "repeat" && count < 0) throw Fail(n, "Repeat count must be non-negative");
            return name == "repeat" ? music.Repeat(count) : music.Transpose(count);
        }

        if (!env.TryGetValue(name, out var item) || item is not Function fn) throw Fail(n, $"Unknown function {name}");
        var parameters = fn.Node.Children.TakeWhile(x => x.Kind == "param").ToArray();
        if (args.Count != parameters.Length) throw Fail(n, $"Wrong number of arguments for {name}");
        var local = new Dictionary<string, object>();
        for (int i = 0; i < parameters.Length; i++)
        {
            var (pn, pt) = ((string, string))parameters[i].Value!;
            Bind(local, pn, Require(parameters[i], args[i], pt), parameters[i]);
        }

        foreach (var stmt in fn.Node.Children.Skip(parameters.Length).SkipLast(1))
            Bind(local, (string)stmt.Value!, Evaluate(stmt.Children[0], Merge(fn.Closure, local)), stmt);
        var result = Evaluate(fn.Node.Children[^1], Merge(fn.Closure, local));
        var (_, returnType) = ((string, string))fn.Node.Value!;
        return Require(fn.Node, result, returnType);
    }

    private static object ResolveQualified(Node n, Dictionary<string, object> env)
    {
        var (module, title, category, member) = ((string?, string, string, string))n.Value!;
        ScoreResult? result;
        if (module == null)
            result = env.GetValueOrDefault("\0score:" + title) as ScoreResult;
        else
        {
            if (env.GetValueOrDefault("\0module:" + module) is not Dictionary<string, ScoreResult> scores)
                throw Fail(n, $"Unknown module {module}");
            result = scores.GetValueOrDefault(title);
        }

        if (result == null) throw Fail(n, $"Unknown score {title}");
        if (category == "fn")
            return result.Functions.GetValueOrDefault(member) ??
                   throw Fail(n, $"Unknown function {member} in score {title}");
        var tracks = result.Score.Tracks.Where(t => t.Name == member).ToArray();
        if (tracks.Length != 1) throw Fail(n, $"Unknown or ambiguous track {member} in score {title}");
        return tracks[0].Music;
    }

    private static string Infer(Node n, Dictionary<string, object> env)
    {
        if (n.Kind == "qualified")
        {
            var value = ResolveQualified(n, env);
            if (((ValueTuple<string?, string, string, string>)n.Value!).Item3 == "track") return "Music";
            var fn = (Function)value;
            var parameters = fn.Node.Children.TakeWhile(c => c.Kind == "param").ToArray();
            if (n.Children.Count != parameters.Length) throw Fail(n, "Wrong number of arguments");
            for (int i = 0; i < parameters.Length; i++)
            {
                if (n.Children[i].Value != null) throw Fail(n.Children[i], "Named arguments are not supported here");
                var expected = ((ValueTuple<string, string>)parameters[i].Value!).Item2;
                var actualType = Infer(n.Children[i].Children[0], env);
                if (!Accepts(actualType, expected)) throw Fail(n.Children[i], $"Expected {expected}, got {actualType}");
            }

            return ((ValueTuple<string, string>)fn.Node.Value!).Item2;
        }

        if (n.Kind == "number")
        {
            var (_, b) = ((BigInteger, BigInteger))n.Value!;
            if (b <= 0) throw Fail(n, "Number denominator must be positive");
            return b == 1 ? "Int" : "Number";
        }

        if (n.Kind == "pitch")
        {
            try
            {
                _ = Pitch.Parse((string)n.Value!).Midi;
            }
            catch (ArgumentException e)
            {
                throw new SemanticException(e.Message, n.Span, e);
            }

            return "Pitch";
        }

        if (n.Kind == "interval") return "Interval";
        if (n.Kind == "name")
            return env.TryGetValue((string)n.Value!, out var v) ? TypeOf(v) : throw Fail(n, $"Unknown name {n.Value}");
        if (n.Kind == "list")
        {
            if (n.Children.Count == 0) throw Fail(n, "Pitch list must not be empty");
            foreach (var c in n.Children)
                if (Infer(c, env) != "Pitch")
                    throw Fail(c, "Expected Pitch");
            return "List<Pitch>";
        }

        if (n.Kind is "+" or "|")
        {
            var a = Infer(n.Children[0], env);
            var b = Infer(n.Children[1], env);
            if (n.Kind == "+" && a == "Pitch" && b == "Interval") return "Pitch";
            if (a != "Music" || b != "Music") throw Fail(n, "Expected Music operands");
            return "Music";
        }

        if (n.Kind is "call" or "pipe")
        {
            var name = (string)n.Value!;
            var args = new List<string>();
            var named = new Dictionary<string, string>();
            bool seen = false;
            int start = 0;
            if (n.Kind == "pipe")
            {
                if (name is not ("repeat" or "transpose")) throw Fail(n, "Pipeline only supports transpose and repeat");
                args.Add(Infer(n.Children[0], env));
                start = 1;
            }

            foreach (var arg in n.Children.Skip(start))
            {
                var ty = Infer(arg.Children[0], env);
                if (arg.Value is null)
                {
                    if (seen) throw Fail(arg, "Positional argument follows named argument");
                    args.Add(ty);
                }
                else
                {
                    seen = true;
                    if (!named.TryAdd((string)arg.Value, ty)) throw Fail(arg, "Duplicate named argument");
                }
            }

            void Check(string actual, string expected)
            {
                if (!Accepts(actual, expected)) throw Fail(n, $"Expected {expected}, got {actual}");
            }

            if (name is "chord" or "sequence")
            {
                if (args.Count != 1 || !named.ContainsKey("duration") ||
                    named.Keys.Any(k => k is not ("duration" or "velocity"))) throw Fail(n, "Invalid named arguments");
                Check(args[0], "List<Pitch>");
                Check(named["duration"], "Number");
                if (named.TryGetValue("velocity", out var ty)) Check(ty, "Int");
                return "Music";
            }

            if (named.Count > 0) throw Fail(n, "Named arguments are not supported here");
            string[] expected;
            string result = "Music";
            int min, max;
            switch (name)
            {
                case "note":
                    expected = ["Pitch", "Number", "Int"];
                    min = 2;
                    max = 3;
                    break;
                case "rest":
                    expected = ["Number"];
                    min = max = 1;
                    break;
                case "repeat":
                case "transpose":
                    expected = ["Music", "Int"];
                    min = max = 2;
                    break;
                default:
                    if (!env.TryGetValue(name, out var item) || item is not Function fn)
                        throw Fail(n, $"Unknown function {name}");
                    expected = fn.Node.Children.TakeWhile(c => c.Kind == "param")
                        .Select(c => ((ValueTuple<string, string>)c.Value!).Item2).ToArray();
                    min = max = expected.Length;
                    result = ((ValueTuple<string, string>)fn.Node.Value!).Item2;
                    break;
            }

            if (args.Count < min || args.Count > max) throw Fail(n, $"Wrong number of arguments for {name}");
            for (int j = 0; j < args.Count; j++) Check(args[j], expected[j]);
            return result;
        }

        throw Fail(n, "Invalid expression");
    }

    private static void Declare(Node n, Dictionary<string, object> outer, Dictionary<string, object> local)
    {
        var parameters = n.Children.TakeWhile(c => c.Kind == "param").ToArray();
        var declared = new HashSet<string>();
        var closure = Merge(outer, local);
        var scope = new Dictionary<string, object>(closure);
        foreach (var p in parameters)
        {
            var (name, type) = ((string, string))p.Value!;
            if (!declared.Add(name) || Builtins.Contains(name)) throw Fail(p, "Duplicate or reserved parameter");
            scope[name] = new TypeValue(type);
        }

        foreach (var stmt in n.Children.Skip(parameters.Length).SkipLast(1))
        {
            var name = (string)stmt.Value!;
            if (!declared.Add(name) || Builtins.Contains(name)) throw Fail(stmt, "Duplicate or reserved local name");
            scope[name] = new TypeValue(Infer(stmt.Children[0], scope));
        }

        var (_, result) = ((string, string))n.Value!;
        var actual = Infer(n.Children[^1], scope);
        if (!Accepts(actual, result)) throw Fail(n.Children[^1], $"Expected {result}, got {actual}");
        Bind(local, ((ValueTuple<string, string>)n.Value!).Item1, new Function(n, closure), n);
    }

    public static CompilationResult CompileSource(string source, string filename = "<string>")
    {
        var active = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        if (filename != "<string>") active.Add(Path.GetFullPath(filename));
        return CompileSourceInternal(source, filename, active);
    }

    private static CompilationResult CompileSourceInternal(string source, string filename, HashSet<string> active)
    {
        var globals = new Dictionary<string, object>();
        var results = new List<ScoreResult>();
        var exports = new List<ExportPlan>();
        foreach (var item in Parser.Parse(source, filename))
        {
            if (item.Kind == "import")
            {
                var (format, importPath) = ((string, string))item.Value!;
                if (format is not ("midi" or "musicxml" or "sonqelra"))
                    throw Fail(item, $"Unknown import format: {format}");
                if (string.IsNullOrEmpty(importPath)) throw Fail(item, "Import path must not be empty");
                if (!Path.IsPathRooted(importPath) && filename == "<string>")
                    throw Fail(item, "Relative import requires a source filename");
                try
                {
                    var target = Path.GetFullPath(Path.IsPathRooted(importPath)
                        ? importPath
                        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filename))!, importPath));
                    var moduleName = Path.GetFileNameWithoutExtension(target);
                    var moduleKey = "\0module:" + moduleName;
                    if (globals.ContainsKey(moduleKey)) throw Fail(item, $"Duplicate module {moduleName}");
                    IReadOnlyList<ScoreResult> imported;
                    if (format == "sonqelra")
                    {
                        if (!active.Add(target)) throw Fail(item, $"Circular import: {target}");
                        try
                        {
                            imported = CompileSourceInternal(File.ReadAllText(target), target, active).Scores;
                        }
                        finally
                        {
                            active.Remove(target);
                        }
                    }
                    else
                        imported =
                        [
                            new ScoreResult(format == "midi" ? Midi.Import(target) : MusicXml.Import(target), null)
                        ];

                    var scores = new Dictionary<string, ScoreResult>();
                    foreach (var importedResult in imported)
                        if (!scores.TryAdd(importedResult.Score.Title, importedResult))
                            throw Fail(item,
                                $"Duplicate score title {importedResult.Score.Title} in module {moduleName}");
                    globals.Add(moduleKey, scores);
                }
                catch (SonqelraException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                               or FormatException or ArithmeticException or System.Xml.XmlException)
                {
                    throw new SemanticException($"{format} import failed: {ex.Message}", item.Span, ex);
                }

                continue;
            }

            if (item.Kind == "fn")
            {
                Declare(item, [], globals);
                continue;
            }

            if (string.IsNullOrEmpty((string)item.Value!)) throw Fail(item, "Score title must not be empty");
            var local = new Dictionary<string, object>();
            var tracks = new List<Track>();
            var plans = new List<Node>();
            var fields = new HashSet<string>();
            KeySignature? key = null;
            Rational tempo = 120;
            var meter = new TimeSignature(4, 4);
            foreach (var n in item.Children)
            {
                var env = Merge(globals, local);
                try
                {
                    switch (n.Kind)
                    {
                        case "tempo":
                        case "meter":
                        case "key":
                            if (!fields.Add(n.Kind)) throw Fail(n, $"Duplicate {n.Kind}");
                            if (n.Kind == "tempo")
                            {
                                tempo = Number(Require(n, Evaluate(n.Children[0], env), "Number"));
                                if (tempo <= 0) throw Fail(n, "Tempo must be positive");
                            }
                            else if (n.Kind == "meter")
                            {
                                var (a, b) = ((int, int))n.Value!;
                                meter = new(a, b);
                            }
                            else
                            {
                                var (root, mode) = ((string, string))n.Value!;
                                if (!Regex.IsMatch(root, @"\A[A-G](?:#+|b+)?\z")) throw Fail(n, "Invalid key root");
                                key = new(root, mode);
                            }

                            break;
                        case "let": Bind(local, (string)n.Value!, Evaluate(n.Children[0], env), n); break;
                        case "fn": Declare(n, globals, local); break;
                        case "melody":
                            if (n.Children.Count == 0) throw Fail(n, "Melody must not be empty");
                            var notes = new List<Music>();
                            foreach (var note in n.Children)
                            {
                                var (pitch, a, b, velocity) = ((string, BigInteger, BigInteger, int?))note.Value!;
                                if (a <= 0 || b <= 0) throw Fail(note, "Duration must be positive");
                                var duration = new Rational(a, b) * 4;
                                if (pitch == "rest")
                                {
                                    if (velocity != null) throw Fail(note, "Rest cannot have velocity");
                                    notes.Add(new Rest(duration));
                                }
                                else
                                {
                                    int vel = velocity ?? 80;
                                    if (vel is < 0 or > 127) throw Fail(note, "Velocity must be 0–127");
                                    try
                                    {
                                        var p = Pitch.Parse(pitch);
                                        _ = p.Midi;
                                        notes.Add(new Note(p, duration, vel));
                                    }
                                    catch (ArgumentException ex)
                                    {
                                        throw new SemanticException(ex.Message, note.Span, ex);
                                    }
                                }
                            }

                            Bind(local, (string)n.Value!, new Sequence(notes), n);
                            break;
                        case "track":
                            if (string.IsNullOrEmpty((string)n.Value!)) throw Fail(n, "Track name must not be empty");
                            var parts = new Dictionary<string, Node>();
                            foreach (var part in n.Children)
                                if (!parts.TryAdd(part.Kind, part))
                                    throw Fail(part, $"Duplicate {part.Kind}");
                            if (!parts.ContainsKey("instrument") || !parts.ContainsKey("play"))
                                throw Fail(n, "Track requires instrument and play");
                            var music = (Music)Require(parts["play"], Evaluate(parts["play"].Children[0], env),
                                "Music");
                            var track = new Track(music, (string)parts["instrument"].Value!, (string)n.Value!,
                                parts.TryGetValue("percussion", out var percussion) && (bool)percussion.Value!);
                            try
                            {
                                _ = track.InstrumentToMidiProgram();
                            }
                            catch (ArgumentException ex)
                            {
                                throw new SemanticException(ex.Message, parts["instrument"].Span, ex);
                            }

                            tracks.Add(track);
                            break;
                        case "import":
                            var (kind, title) = ((string, string))n.Value!;
                            if (kind != "score") throw Fail(n, "Only import score is allowed inside a score");
                            var matching = results.Where(r => r.Score.Title == title).ToArray();
                            if (matching.Length != 1) throw Fail(n, $"Unknown or ambiguous score {title}");
                            if (!local.TryAdd("\0score:" + title, matching[0]))
                                throw Fail(n, $"Duplicate score import {title}");
                            break;
                        case "export": plans.Add(n); break;
                    }
                }
                catch (SonqelraException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ArgumentException or ArithmeticException or OverflowException)
                {
                    throw new SemanticException(ex.Message, n.Span, ex);
                }
            }

            if (tracks.Count == 0) throw Fail(item, "Score must have at least one track");
            var result =
                new ScoreResult(
                    new Score(tracks, (string)item.Value!, new TempoMap([(0, tempo)]), new MeterMap([(0, meter)])),
                    key)
                {
                    Functions = local.Where(x => x.Value is Function).ToDictionary(x => x.Key, x => (Function)x.Value)
                };
            results.Add(result);
            foreach (var plan in plans)
            {
                var (format, path) = ((string, string))plan.Value!;
                exports.Add(new ExportPlan(result, path, plan.Span, format));
            }
        }

        return new(results.AsReadOnly(), exports.AsReadOnly());
    }
}