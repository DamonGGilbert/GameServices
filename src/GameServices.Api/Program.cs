using GameServices.Api.Auth;
using GameServices.Api.Catalog;
using GameServices.Api.Chat;
using GameServices.Api.Guilds;
using GameServices.Api.Map;
using GameServices.Api.Social;
using GameServices.Api.Economy;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Online;
using GameServices.Api.Saves;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Progress;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGameServices(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddHostedService<BossLifecycleService>();
builder.Services.AddHostedService<WalkingLifecycleService>();

// The list of cards that exist (exported from Unity). Loaded once at startup.
string catalogPath = builder.Configuration["Catalog:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "Catalog", "catalog.json");
builder.Services.AddSingleton(CardCatalog.LoadFile(catalogPath));

// The walking game's monsters (exported from Unity), and the rules for finding them by walking.
string monsterCatalogPath = builder.Configuration["Monsters:CatalogPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "Walking", "monster-catalog.json");
builder.Services.AddSingleton(GameServices.Api.Walking.Monsters.MonsterCatalog.LoadFile(monsterCatalogPath));
builder.Services.AddSingleton<GameServices.Api.Walking.Monsters.MonsterFinder>();

var app = builder.Build();

// Bring the database up to date. A "migration" is a small, versioned script that changes the
// database structure (add a table, add a column). EF Core remembers which ones have run, so
// every machine - your PC, a test, the production server - ends up with the same structure.
using (var scope = app.Services.CreateScope())
{
    AppDbContext startupDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    startupDb.Database.Migrate();
    // Monsters made under older rules (e.g. seven rarities, before there were five) brought up to date, once.
    GameServices.Api.Walking.Monsters.MonsterRulesUpgrade.Run(startupDb);
    // The list of words to block: filled the first time, then read from the database.
    await GameServices.Api.Moderation.BlockedWords.SeedIfEmptyAsync(startupDb);
    await GameServices.Api.Moderation.BlockedWords.RefreshAsync(startupDb);
}

// Admin commands (run on the server machine): dotnet run -- reset-password SomeUser
if (await AdminCommands.TryRun(app.Services, args))
{
    return;
}

// ...reading reports: dotnet run -- reports
if (await GameServices.Api.Moderation.ReportAdmin.TryRun(app.Services, args))
{
    return;
}

// ...checking every Glimmer adds up: dotnet run -- audit
if (await GameServices.Api.Walking.Wallet.WalletAudit.TryRunAdmin(app.Services, args))
{
    return;
}

// ...reading what a walker has done: dotnet run -- player SomeName 7
if (await GameServices.Api.Walking.Progress.ActivityLog.TryRunAdmin(app.Services, args))
{
    return;
}

// ...and the list of blocked words: dotnet run -- words
if (await GameServices.Api.Moderation.BlockedWords.TryRunAdmin(app.Services, args))
{
    return;
}

// ---- The request pipeline: every request passes through these, in order ----
app.UseExceptionHandler();      // turn unexpected crashes into a clean 500 instead of leaking details
app.UseStatusCodePages();
app.UseWebSockets();            // duel sockets (/ws/duel for phones, /ws/host for the duel host)
app.UseAuthentication();        // "who are you?"  (reads the access token)
app.UseRateLimiter();           // slow down abuse (after authentication: each player has their own limit)
app.UseAuthorization();         // "are you allowed?"
app.UseActivityLog();           // writes down what each walker changed (Walking/Progress/ActivityLog)

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
app.MapWalkingEndpoints();
app.MapShowcaseEndpoints();
app.MapAvatarEndpoints();
app.MapCosmeticImageEndpoints();
app.MapProfileCardEndpoints();
app.MapOnlineEndpoints();
app.MapMapEndpoints();
GameServices.Api.Moderation.ReportEndpoints.MapReportEndpoints(app);
GameServices.Api.Legal.LegalEndpoints.MapLegalEndpoints(app);
app.MapHub<ChatHub>("/hubs/chat");

app.Run();

// Lets the test project start the whole server in memory.
public partial class Program;
