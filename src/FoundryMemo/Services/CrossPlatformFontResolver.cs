// Copyright (c) foundry-memo. All rights reserved.

using PdfSharp.Fonts;

namespace FoundryMemo.Services;

/// <summary>
/// Cross-platform font resolver for PdfSharp 6.x.
/// Resolves fonts from system locations on Windows and Linux.
/// </summary>
public class CrossPlatformFontResolver : IFontResolver
{
    private static readonly Dictionary<string, string[]> FontFileMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf"],
        ["Arial Bold"] = ["arialbd.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf"],
        ["Arial Italic"] = ["ariali.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf"],
        ["Arial Bold Italic"] = ["arialbi.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"],
    };

    private static readonly string[] FontDirectories = GetFontDirectories();

    private static string[] GetFontDirectories()
    {
        var dirs = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts"));
        }
        else
        {
            dirs.AddRange([
                "/usr/share/fonts/truetype",
                "/usr/share/fonts/truetype/liberation",
                "/usr/share/fonts/truetype/dejavu",
                "/usr/share/fonts",
                "/usr/local/share/fonts",
            ]);

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                dirs.Add(Path.Combine(home, ".fonts"));
        }

        return dirs.Where(Directory.Exists).ToArray();
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var key = familyName;
        if (isBold && isItalic) key += " Bold Italic";
        else if (isBold) key += " Bold";
        else if (isItalic) key += " Italic";

        // Try exact match first, then base family
        if (FontFileMap.ContainsKey(key))
            return new FontResolverInfo(key);

        if (FontFileMap.ContainsKey(familyName))
            return new FontResolverInfo(familyName);

        // Fall back to Arial
        var fallback = "Arial";
        if (isBold && isItalic) fallback = "Arial Bold Italic";
        else if (isBold) fallback = "Arial Bold";
        else if (isItalic) fallback = "Arial Italic";

        return new FontResolverInfo(fallback);
    }

    public byte[]? GetFont(string faceName)
    {
        if (!FontFileMap.TryGetValue(faceName, out var candidates))
            candidates = FontFileMap["Arial"];

        foreach (var candidate in candidates)
        {
            var path = FindFontFile(candidate);
            if (path is not null)
                return File.ReadAllBytes(path);
        }

        return null;
    }

    private static string? FindFontFile(string fileName)
    {
        foreach (var dir in FontDirectories)
        {
            // Direct match
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
                return path;

            // Search subdirectories
            try
            {
                var found = Directory.GetFiles(dir, fileName, SearchOption.AllDirectories);
                if (found.Length > 0)
                    return found[0];
            }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>
    /// Registers this resolver with PdfSharp. Call once at startup.
    /// </summary>
    public static void Register()
    {
        if (GlobalFontSettings.FontResolver is not CrossPlatformFontResolver)
            GlobalFontSettings.FontResolver = new CrossPlatformFontResolver();
    }
}
