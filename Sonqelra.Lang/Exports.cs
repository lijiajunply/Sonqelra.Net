using Sonqelra.Core;
using Sonqelra.Backends;

namespace Sonqelra.Lang;

public static class Exports
{
    private static string ResolveTarget(string path, string baseDirectory)
    {
        var full = Path.GetFullPath(path, Path.GetFullPath(baseDirectory));
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var segment in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            try
            {
                var resolved = info.ResolveLinkTarget(true);
                if (resolved is not null) current = ResolveTarget(resolved.FullName, root);
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return Path.GetFullPath(current);
    }

    public static void Execute(CompilationResult result, string baseDirectory,
        IReadOnlyDictionary<string, Action<Score, string>>? exporters = null)
    {
        var available = new Dictionary<string, Action<Score, string>>(StringComparer.Ordinal)
            { { "midi", Midi.Export }, { "musicxml", MusicXml.Export } };
        if (exporters is not null)
            foreach (var (name, exporter) in exporters)
                available[name] = exporter;
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var targets = new List<(ExportPlan Plan, string Target, Action<Score, string> Exporter)>();
        foreach (var plan in result.Exports)
        {
            if (!available.TryGetValue(plan.Format, out var exporter))
                throw new ExportException($"Unknown export format: {plan.Format}", plan.Span);
            var target = ResolveTarget(plan.Path, baseDirectory);
            if (!seen.Add(target)) throw new ExportException($"Duplicate export target: {target}", plan.Span);
            targets.Add((plan, target, exporter));
        }

        foreach (var (plan, target, exporter) in targets)
        {
            try
            {
                exporter(plan.Score.Score, target);
            }
            catch (Exception e)
            {
                throw new ExportException($"{plan.Format} export failed: {e.Message}", plan.Span, e);
            }
        }
    }
}