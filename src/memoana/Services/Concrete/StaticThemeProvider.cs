using System.Text.Json;
using memoana.Services.Abstract;

namespace memoana.Services.Concrete;

/// <summary>Reads the static metadata and WebP resources from the local submodule or GitHub Pages.</summary>
public sealed class StaticThemeProvider(IConfiguration configuration, IWebHostEnvironment environment, HttpClient httpClient) : IThemeProvider
{
    public IReadOnlyList<ThemeAsset> SelectAssets(int count)
    {
        var themeId = configuration["Themes:DefaultTheme"] ?? "01a0bbbe-e0f4-7251-86c8-cc9bc84703d0";
        var root = configuration["Themes:RootPath"];
        var baseUrl = configuration["Themes:BaseUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(root))
            return SelectFromFileSystem(count, themeId, root);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Themes requires either RootPath or BaseUrl.");
        return SelectFromStaticSite(count, themeId, baseUrl);
    }

    private IReadOnlyList<ThemeAsset> SelectFromFileSystem(int count, string themeId, string configuredRoot)
    {
        var root = Path.IsPathRooted(configuredRoot) ? configuredRoot : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configuredRoot));
        var manifestJson = File.ReadAllText(Path.Combine(root, "data", themeId, "manifest.json"));
        var cardsJson = File.ReadAllText(Path.Combine(root, "data", themeId, "cards.json"));
        return SelectAssets(count, themeId, manifestJson, cardsJson, file => File.ReadAllBytes(Path.Combine(root, "assets", themeId, file)));
    }

    private IReadOnlyList<ThemeAsset> SelectFromStaticSite(int count, string themeId, string baseUrl)
    {
        var manifestJson = httpClient.GetStringAsync($"{baseUrl}/data/{themeId}/manifest.json").GetAwaiter().GetResult();
        var cardsJson = httpClient.GetStringAsync($"{baseUrl}/data/{themeId}/cards.json").GetAwaiter().GetResult();
        return SelectAssets(count, themeId, manifestJson, cardsJson, file =>
        {
            var bytes = httpClient.GetByteArrayAsync($"{baseUrl}/assets/{themeId}/{file}").GetAwaiter().GetResult();
            return bytes;
        });
    }

    private static IReadOnlyList<ThemeAsset> SelectAssets(int count, string themeId, string manifestJson, string cardsJson, Func<string, byte[]> readAsset)
    {
        using var manifest = JsonDocument.Parse(manifestJson);
        if (!string.Equals(manifest.RootElement.GetProperty("id").GetString(), themeId, StringComparison.OrdinalIgnoreCase) ||
            manifest.RootElement.GetProperty("cardCount").GetInt32() < count)
            throw new InvalidOperationException("The configured theme does not provide enough valid cards.");

        using var cards = JsonDocument.Parse(cardsJson);
        var entries = cards.RootElement.GetProperty("cards").EnumerateArray().ToArray();
        if (entries.Length < count) throw new InvalidOperationException("The configured theme has fewer cards than required.");
        return entries.OrderBy(_ => Random.Shared.Next()).Take(count).Select(entry =>
        {
            var id = entry.GetProperty("id").GetString()!;
            var file = entry.GetProperty("file").GetString()!;
            var bytes = readAsset(file);
            if (bytes.Length == 0) throw new InvalidOperationException($"Theme asset '{id}' is empty.");
            return new ThemeAsset(id, "image/webp", bytes);
        }).ToArray();
    }
}
