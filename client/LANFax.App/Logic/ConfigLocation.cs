namespace LANFax.App.Logic;

/// <summary>Where config.json lives: ProgramData when writable by this user, otherwise LocalAppData.</summary>
public sealed record ConfigLocation(string Path, bool IsFallback, string ReadPath)
{
    public static ConfigLocation Default() => Choose(
        Core.ClientConfig.DefaultPath,
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LANFax", "config.json"));

    public static ConfigLocation Choose(string primary, string fallback)
    {
        if (CanWrite(System.IO.Path.GetDirectoryName(primary)!)) return new ConfigLocation(primary, false, primary);
        // Not writable: save to the fallback, but until one exists keep reading the machine-wide file.
        var read = !File.Exists(fallback) && File.Exists(primary) ? primary : fallback;
        return new ConfigLocation(fallback, true, read);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = System.IO.Path.Combine(dir, ".w-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }
}
