using memoana.Services.Abstract;
using memoana.Services.Concrete;
using memoana.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
var databasePath = builder.Configuration["Persistence:DatabasePath"] ?? "data/memoana.db";
if (!Path.IsPathRooted(databasePath)) databasePath = Path.Combine(builder.Environment.ContentRootPath, databasePath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
builder.Services.AddDbContextFactory<MemoAnaDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton<IGameStateStore, SqliteGameStateStore>();
builder.Services.AddSingleton<IThemeProvider, StaticThemeProvider>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<IGameService>(sp => sp.GetRequiredService<GameService>());
builder.Services.AddSingleton<IGameAssetService, GameAssetService>();
builder.Services.AddHostedService<memoana.Services.Concrete.GameExpiryService>();
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<MemoAnaDbContext>().Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthorization();

app.MapControllers();
app.MapHub<memoana.Hubs.GameHub>("/gameHub");
app.Run();

public partial class Program;

