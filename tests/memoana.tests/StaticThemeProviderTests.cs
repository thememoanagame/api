using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using memoana.Services.Concrete;
using memoana.Contracts;

namespace memoana.tests;

public sealed class StaticThemeProviderTests
{
    [Fact]
    public void RealThemesSubmoduleProvidesValidatedWebpAssets()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "modules", "themes", "src", "themes", "wwwroot"));
        Assert.True(Directory.Exists(root), $"Themes submodule is not initialized at {root}.");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Themes:RootPath"] = root,
            ["Themes:DefaultTheme"] = "01a0bbbe-e0f4-7251-86c8-cc9bc84703d0"
        }).Build();

        var provider = new StaticThemeProvider(configuration, new TestEnvironment { ContentRootPath = root });
        var assets = provider.SelectAssets(6);

        Assert.Equal(6, assets.Count);
        Assert.All(assets, asset =>
        {
            Assert.Equal("image/webp", asset.ContentType);
            Assert.NotEmpty(asset.Content);
            Assert.True(Guid.TryParse(asset.SourceId, out _));
        });
    }

    [Fact]
    public void RealThemesCatalogContainsOnlyUsableThemes()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "modules", "themes", "src", "themes", "wwwroot"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Themes:RootPath"] = root,
            ["Themes:DefaultTheme"] = "01a0bbbe-e0f4-7251-86c8-cc9bc84703d0"
        }).Build();
        var provider = new StaticThemeProvider(configuration, new TestEnvironment { ContentRootPath = root });
        var themes = provider.ListThemes();

        Assert.Equal(4, themes.Count);
        Assert.All(themes, theme =>
        {
            Assert.True(theme.Available);
            Assert.Equal(15, theme.CardCount);
            Assert.Contains(GameDifficulty.Hard, theme.SupportedDifficulties);
            Assert.True(Guid.TryParse(theme.Id, out _));
        });
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "memoana.tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
