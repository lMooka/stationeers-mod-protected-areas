using System;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

// usage: decomp <dll> <outdir> list            -> writes all type full names to types.txt
//        decomp <dll> <outdir> Type1 Type2 ... -> decompiles each type (by Name or FullName) into outdir
class Program
{
    static void Main(string[] args)
    {
        string path = args[0];
        string outDir = args[1];
        Directory.CreateDirectory(outDir);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(path, settings);
        var types = decompiler.TypeSystem.MainModule.TypeDefinitions.ToList();
        if (args[2] == "list")
        {
            File.WriteAllLines(Path.Combine(outDir, "types.txt"), types.Select(t => t.FullName).OrderBy(x => x));
            Console.WriteLine($"{types.Count} types");
            return;
        }
        foreach (var name in args.Skip(2))
        {
            var matches = types.Where(t => t.FullName == name || t.Name == name).ToList();
            if (matches.Count == 0) { Console.WriteLine($"NOT FOUND: {name}"); continue; }
            foreach (var t in matches)
            {
                if (t.DeclaringType != null) continue;
                try
                {
                    var code = decompiler.DecompileTypeAsString(new FullTypeName(t.FullName));
                    var file = Path.Combine(outDir, t.FullName.Replace('`', '_') + ".cs");
                    File.WriteAllText(file, code);
                    Console.WriteLine($"OK {t.FullName} -> {code.Split('\n').Length} lines");
                }
                catch (Exception e) { Console.WriteLine($"FAIL {t.FullName}: {e.Message}"); }
            }
        }
    }
}
