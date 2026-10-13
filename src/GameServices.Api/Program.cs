using GameServices.Api.Auth;
using GameServices.Api.Catalog;
using GameServices.Api.Chat;
using GameServices.Api.Guilds;
using GameServices.Api.Social;
using GameServices.Api.Economy;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Online;
using GameServices.Api.Saves;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGameServices(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddHostedService<BossLifecycleService>();

// The list of cards that exist (exported from Unity). Loaded once at startup.
string catalogPath = builder.Configuration["Catalog:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "Catalog", "catalog.json");
builder.Services.AddSingleton(CardCatalog.LoadFile(catalogPath));

var app = builder.Build();

// Bring the database up to date. A "migration" is a small, versioned script that changes the
// database structure (add a table, add a column). EF Core remembers which ones have run, so
// every machine - your PC, a test, the production server - ends up with the same structure.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// Admin commands (run on the server machine): dotnet run -- reset-password SomeUser
if (await AdminCommands.TryRun(app.Services, args))
{
    return;
}

// ---- The request pipeline: every request passes through these, in order ----
app.UseExceptionHandler();      // turn unexpected crashes into a clean 500 instead of leaking details
app.UseStatusCodePages();
app.UseRateLimiter();           // slow down abuse
app.UseWebSockets();            // duel sockets (/ws/duel for phones, /ws/host for the duel host)
app.UseAuthentication();        // "who are you?"  (reads the access token)
app.UseAuthorization();         // "are you allowed?"

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();                 // /openapi/v1.json
    app.MapScalarApiReference();      // /scalar/v1  <- a friendly page to try the API in your browser
}

// A simple "is the server alive?" check. Hosting platforms and monitoring tools call this.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timeUtc = DateTime.UtcNow }))
    .WithTags("Operations");

app.MapAuthEndpoints();
app.MapSaveEndpoints();
app.MapEconomyEndpoints();
app.MapSocialEndpoints();
app.MapChatEndpoints();
app.MapGuildEndpoints();
app.MapShowcaseEndpoints();
app.MapAvatarEndpoints();
app.MapOnlineEndpoints();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Lets the test project start the whole server in memory.
public partial class Program;
