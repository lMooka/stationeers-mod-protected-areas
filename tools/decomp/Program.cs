using System.Collections.Generic;
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
    static void FindCallers(string path, string typeName, string methodName)
    {
        using var stream = File.OpenRead(path);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var targets = new HashSet<int>();
        foreach (var handle in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(handle);
            var type = md.GetTypeDefinition(def.GetDeclaringType());
            if (md.GetString(def.Name) == methodName && md.GetString(type.Name) == typeName)
            {
                targets.Add(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(handle));
            }
        }
        Console.WriteLine($"{targets.Count} target method(s)");
        foreach (var handle in md.MethodDefinitions)
        {
            var def = md.GetMethodDefinition(handle);
            if (def.RelativeVirtualAddress == 0) continue;
            byte[] il;
            try { il = System.Reflection.Metadata.PEReaderExtensions.GetMethodBody(pe, def.RelativeVirtualAddress).GetILBytes(); } catch { continue; }
            if (il == null) continue;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                if (targets.Contains(BitConverter.ToInt32(il, i + 1)))
                {
                    var type = md.GetTypeDefinition(def.GetDeclaringType());
                    string ns = md.GetString(type.Namespace);
                    string declaring = type.GetDeclaringType().IsNil ? "" : md.GetString(md.GetTypeDefinition(type.GetDeclaringType()).Name) + "+";
                    Console.WriteLine($"{(ns.Length > 0 ? ns + "." : "")}{declaring}{md.GetString(type.Name)}::{md.GetString(def.Name)}");
                    break;
                }
            }
        }
    }

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
        if (args[2] == "callers")
        {
            // decomp <dll> <outdir> callers <TypeName> <MethodName>: lista os metodos que chamam TypeName.MethodName
            FindCallers(path, args[3], args[4]);
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
