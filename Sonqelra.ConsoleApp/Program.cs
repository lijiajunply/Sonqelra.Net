using Sonqelra.Lang;
using System.Text;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: sonqelra <source.sonqelra>");
    return 1;
}
try
{
    var path = Path.GetFullPath(args[0]);
    var source = File.ReadAllText(path, new UTF8Encoding(false, true));
    var result = Compiler.CompileSource(source, path);
    Exports.Execute(result, Path.GetDirectoryName(path)!);
    return 0;
}
catch (Exception ex) when (ex is SonqelraException or IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
