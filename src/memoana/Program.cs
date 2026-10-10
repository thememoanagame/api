using memoana.Services.Abstract;
using memoana.Services.Concrete;
using memoana.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();
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

app.MapHealthChecks("/api/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";

        var response = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                error = entry.Value.Exception?.Message
            })
        };

        await context.Response.WriteAsJsonAsync(response);
    }
});

app.MapControllers();
app.MapHub<memoana.Hubs.GameHub>("/gameHub");
app.Run();

public partial class Program;

