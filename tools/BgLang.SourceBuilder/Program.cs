using System.Text.Json;
using System.Text;

namespace BgLang.SourceBuilder;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool build = args.Length == 4 && args[0] == "build";
        bool export = args.Length == 3 && args[0] == "export";
        bool stressOverlay = args.Length == 5 && args[0] == "apply-stress-overlay";
        bool retainLemmas = args.Length == 4 && args[0] == "retain-lemmas";
        bool promoteNative = args.Length == 4 && args[0] == "promote-native";
        bool correctStored = args.Length == 4 && args[0] == "correct-stored";
        bool measureOpen = args.Length == 2 && args[0] == "measure-open";
        bool remove = args.Length >= 4 && args[0] == "remove" && (args.Length == 4 ||
            args.Length == 5 && args[4] == "--drop-inbound-phrases" || args.Length == 6 && args[4] == "--transfer-to");
        bool carryStress = args.Length is 4 or 5 && args[0] == "carry-stress" &&
            (args.Length == 4 || args[4] == "--replace-form-stress");
        bool compactStress = args.Length == 4 && args[0] == "compact-stress";
        bool importStress = args.Length == 7 && args[0] == "import-legacy-stress";
        if (!build && !export && !remove && !carryStress && !retainLemmas && !promoteNative && !correctStored && !measureOpen && !importStress && !compactStress && !stressOverlay)
        {
            Console.Error.WriteLine("Употреба: build <lexemes.source.jsonl> <reviewed-graph.json> <new-output-directory>");
            Console.Error.WriteLine("Или: export <manifest.json> <new-source-directory>");
            Console.Error.WriteLine("Или: apply-stress-overlay <authoring.json> <graph.json> <overlay.json> <new-source-directory>");
            Console.Error.WriteLine("Или: retain-lemmas <manifest.json> <source-bound-selection.json> <new-package-directory>");
            Console.Error.WriteLine("Или: promote-native <manifest.json> <current-graph.json> <new-package-directory>");
            Console.Error.WriteLine("Или: correct-stored <manifest.json> <source-bound-replacements.json> <new-package-directory>");
            Console.Error.WriteLine("Или: measure-open <manifest.json>");
            Console.Error.WriteLine("Или: remove <manifest.json> <entryId> <new-package-directory> [--transfer-to <entryId> | --drop-inbound-phrases]");
            Console.Error.WriteLine("Или: carry-stress <manifest.json> <entryId> <new-package-directory> [--replace-form-stress]");
            Console.Error.WriteLine("Или: compact-stress <source.json> <graph.json> <new-source-directory>");
            Console.Error.WriteLine("Или: import-legacy-stress <source.json> <graph.json> <owner-map.jsonl> <captured-observations.jsonl> <legacy-stress-v1:hash> <new-source-directory>");
            Console.Error.WriteLine("--replace-form-stress премахва всички стари non-base корекции и ги изчислява отново; точният base seed остава.");
            return 2;
        }
        try
        {
            object result = stressOverlay ? AuthoringStressOverlay.Apply(args[1], args[2], args[3], args[4]) : compactStress ? LegacyStressCompactor.Compact(args[1], args[2], args[3]) : importStress ? LegacyStressImporter.Import(args[1], args[2], args[3], args[4], args[5], args[6]) :
                correctStored ? StoredSourceCorrection.Correct(args[1], args[2], args[3]) : measureOpen ? PackageOpenMeasurement.Run(args[1]) : promoteNative
                ? NativeSourcePromotion.Promote(args[1], args[2], args[3]) : retainLemmas ? StoredLemmaMigration.RetainLemmas(args[1], args[2], args[3]) : build ? NeutralSourceBuilder.Build(args[1], args[2], args[3]) : export
                ? NeutralSourceEditor.Export(args[1], args[2])
                : remove ? NeutralSourceEditor.Remove(args[1], args[2], args[3], args.Length == 6 ? args[5] : null, args.Length == 5)
                : NeutralSourceEditor.CarryStress(args[1], args[2], args[3], args.Length == 5);
            // Publication has already committed; a broken output pipe must not roll it back.
            Console.WriteLine(JsonSerializer.Serialize(result, NeutralSourceBuilder.JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or
                                             InvalidOperationException or UnauthorizedAccessException or JsonException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine("Пакетът не е завършен или потвърждението не може да се изведе: " + exception.Message);
            return 1;
        }
    }
}
