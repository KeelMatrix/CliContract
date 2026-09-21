using KeelMatrix.CliContract.Core;

if (args.Length != 4 || !string.Equals(args[0], "normalize", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Phase 0 probe usage: normalize <opencli|dotnet> <input> <output>");
    return 2;
}

var adapter = args[1];
var inputPath = Path.GetFullPath(args[2]);
var outputPath = Path.GetFullPath(args[3]);

try
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine("INPUT_NOT_FOUND: Input file does not exist.");
        return 2;
    }

    var input = File.ReadAllText(inputPath);
    var manifest = Normalizer.Normalize(adapter, input);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, Normalizer.Serialize(manifest));
    Console.WriteLine($"normalized adapter={manifest.Adapter} schema={manifest.SchemaVersion} output={outputPath}");
    return 0;
}
catch (NormalizationException exception)
{
    Console.Error.WriteLine($"{exception.Code}: {exception.Message}");
    return 3;
}
catch (Exception)
{
    Console.Error.WriteLine("INTERNAL_ERROR: The probe could not complete.");
    return 4;
}
