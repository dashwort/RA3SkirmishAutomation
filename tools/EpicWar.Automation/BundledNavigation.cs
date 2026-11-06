using System.Reflection;
using System.Security.Cryptography;

namespace EpicWar.Automation;

internal static class BundledNavigation
{
    internal const string Sha256="72369DF72E3CEFFA758C6C671520AC1DE9F8FA61E1DD730B6237CADBCF31C388";
    internal static bool Matches(string path)
    {
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))==Sha256;
    }
    internal static string Extract(string? root=null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EpicWar.Automation","Patches","navigation-once-20261029");
        Directory.CreateDirectory(root);
        var destination=Path.Combine(root,"Epic-War-navigation-once-20261029.big");
        if(File.Exists(destination))
        {
            if(!Matches(destination))throw new InvalidDataException("The cached navigation patch differs from its published hash. Inspect it before replacing it: "+destination);
            return destination;
        }
        using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream("EpicWar.Automation.Navigation.big")
            ?? throw new InvalidOperationException("The bundled navigation patch is missing.");
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".new";
        try
        {
            using(var output=File.Create(temporary))source.CopyTo(output);
            if(!Matches(temporary))throw new InvalidDataException("The bundled navigation patch failed its pinned SHA-256 check.");
            try{File.Move(temporary,destination);}catch(IOException)when(File.Exists(destination)&&Matches(destination)){}
            return destination;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
