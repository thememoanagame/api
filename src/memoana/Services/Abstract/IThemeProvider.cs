namespace memoana.Services.Abstract;

/// <summary>Provides validated theme assets without exposing the theme repository to the game engine.</summary>
public interface IThemeProvider
{
    IReadOnlyList<ThemeAsset> SelectAssets(int count);
}

public sealed record ThemeAsset(string SourceId, string ContentType, byte[] Content);
