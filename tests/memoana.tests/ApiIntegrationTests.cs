using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using memoana.Services.Abstract;
using memoana.Contracts;

namespace memoana.tests;

public sealed class ApiIntegrationTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly ApiFactory _factory;

    public ApiIntegrationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RestRoomCreationAndAssetAccessUseOpaqueParticipationToken()
    {
        using var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("api/game/rooms", new { Mode = "Time", Difficulty = "Easy" });
        var room = await create.Content.ReadFromJsonAsync<CreateRoomResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(room);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/game/rooms/{room!.RoomId}/assets")).StatusCode);
    }

    [Fact]
    public async Task SignalRJoinIssuesTokenAndRestAcceptsOnlyThatToken()
    {
        using var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("api/game/rooms", new { Mode = "Time", Difficulty = "Easy" });
        var room = (await create.Content.ReadFromJsonAsync<CreateRoomResponse>(Json))!;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "gameHub"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();
        await connection.StartAsync();

        var result = await connection.InvokeAsync<GameOperationResult>("JoinRoom", room.RoomId);
        Assert.True(result.Succeeded, $"{result.ErrorCode}: {result.ErrorMessage}");
        var joined = ((JsonElement)result.Value!).Deserialize<JoinRoomResponse>(Json)!;
        Assert.True(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(joined.AccessToken));

        using var valid = new HttpRequestMessage(HttpMethod.Get, $"api/game/rooms/{room.RoomId}/assets");
        valid.Headers.Add("X-Player-Token", joined.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(valid)).StatusCode);

        using var invalid = new HttpRequestMessage(HttpMethod.Get, $"api/game/rooms/{room.RoomId}/assets");
        invalid.Headers.Add("X-Player-Token", joined.PlayerId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(invalid)).StatusCode);
    }

    [Fact]
    public async Task RoomStateRequiresTheParticipationToken()
    {
        using var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("api/game/rooms", new { Mode = "Time", Difficulty = "Easy" });
        var room = (await create.Content.ReadFromJsonAsync<CreateRoomResponse>(Json))!;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/game/rooms/{room.RoomId}")).StatusCode);
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        builder.UseSetting(WebHostDefaults.ContentRootKey, repositoryRoot);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Themes:RootPath"] = Path.Combine(repositoryRoot, "modules", "themes", "src", "themes", "wwwroot")
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IThemeProvider>();
            services.AddSingleton<IThemeProvider, IntegrationThemeProvider>();
        });
        builder.UseTestServer();
    }
}

public sealed class IntegrationThemeProvider : IThemeProvider
{
    public IReadOnlyList<ThemeAsset> SelectAssets(int count) => Enumerable.Range(0, count)
        .Select(x => new ThemeAsset(Guid.NewGuid().ToString(), "image/webp", [1, 2, 3])).ToArray();
}
