using memoana.Services.Abstract;
using memoana.Services.Concrete;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IThemeProvider, StaticThemeProvider>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddSingleton<IGameService>(sp => sp.GetRequiredService<GameService>());
builder.Services.AddSingleton<IGameAssetService, GameAssetService>();
builder.Services.AddHostedService<memoana.Services.Concrete.GameExpiryService>();
var app = builder.Build();

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

