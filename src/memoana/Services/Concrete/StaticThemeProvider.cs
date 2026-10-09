using System.Text.Json;
using memoana.Services.Abstract;

namespace memoana.Services.Concrete;

/// <summary>Reads the generated static theme contract from the checked-out themes submodule.</summary>
public sealed class StaticThemeProvider(IConfiguration configuration, IWebHostEnvironment environment) : IThemeProvider
{
    public IReadOnlyList<ThemeAsset> SelectAssets(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        var themeId = configuration["Themes:DefaultTheme"] ?? throw new InvalidOperationException("Themes:DefaultTheme is required.");
        if (!Guid.TryParse(themeId, out var parsedThemeId)) throw new InvalidOperationException($"The configured theme id '{themeId}' is not a GUID.");
        var configuredRoot = configuration["Themes:RootPath"] ?? throw new InvalidOperationException("Themes:RootPath must point to the local themes submodule.");
        var root = Path.GetFullPath(Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.Combine(environment.ContentRootPath, configuredRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The local themes root does not exist: {root}");

        var canonicalThemeId = parsedThemeId.ToString("D");
        var themeRoot = Path.Combine(root, "assets", canonicalThemeId);
        var dataRoot = Path.Combine(root, "data", canonicalThemeId);
        var manifestPath = Path.Combine(dataRoot, "manifest.json");
        var cardsPath = Path.Combine(dataRoot, "cards.json");
        if (!File.Exists(manifestPath) || !File.Exists(cardsPath)) throw new InvalidOperationException($"Theme '{canonicalThemeId}' is missing generated metadata.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestRoot = manifest.RootElement;
        if (!string.Equals(manifestRoot.GetProperty("id").GetString(), canonicalThemeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifestRoot.GetProperty("assetDirectory").GetString(), $"/assets/{canonicalThemeId}/", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifestRoot.GetProperty("validationStatus").GetString(), "valid", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Theme '{canonicalThemeId}' has an invalid manifest.");

        using var cards = JsonDocument.Parse(File.ReadAllText(cardsPath));
        var entries = cards.RootElement.GetProperty("cards").EnumerateArray().ToArray();
        var declaredCount = manifestRoot.GetProperty("cardCount").GetInt32();
        if (declaredCount != entries.Length || entries.Length < count) throw new InvalidOperationException($"Theme '{canonicalThemeId}' provides {entries.Length} cards; {count} are required.");

        var available = entries.Select(entry =>
        {
            var sourceId = entry.GetProperty("id").GetString();
            var file = entry.GetProperty("file").GetString();
            var url = entry.GetProperty("url").GetString();
            if (!Guid.TryParse(sourceId, out _) || string.IsNullOrWhiteSpace(file) || !string.Equals(url, $"/assets/{canonicalThemeId}/{file}", StringComparison.Ordinal)) throw new InvalidOperationException($"Theme '{canonicalThemeId}' contains invalid card metadata.");
            var fullPath = Path.GetFullPath(Path.Combine(themeRoot, file));
            if (!fullPath.StartsWith(Path.GetFullPath(themeRoot + Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(fullPath), ".webp", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) throw new InvalidOperationException($"Theme card '{file}' is not a valid local WebP asset.");
            var content = File.ReadAllBytes(fullPath);
            if (content.Length == 0) throw new InvalidOperationException($"Theme card '{file}' is empty.");
            return new ThemeAsset(sourceId!, "image/webp", content);
        }).ToArray();
        return available.OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
    }
}
