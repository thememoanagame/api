using memoana.Contracts;

namespace memoana.Services.Abstract;

/// <summary>Provides validated theme assets without exposing the theme repository to the game engine.</summary>
public interface IThemeProvider
{
    string? DefaultThemeId => null;
    IReadOnlyList<ThemeSummary> ListThemes() => [];
    IReadOnlyList<ThemeAsset> SelectAssets(int count);
    IReadOnlyList<ThemeAsset> SelectAssets(string themeId, int count) => SelectAssets(count);
}

public sealed record ThemeAsset(string SourceId, string ContentType, byte[] Content);
