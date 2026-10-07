using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Saves;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGameServices(builder.Configuration);

var app = builder.Build();

// Bring the database up to date. A "migration" is a small, versioned script that changes the
// database structure (add a table, add a column). EF Core remembers which ones have run, so
// every machine - your PC, a test, the production server - ends up with the same structure.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// ---- The request pipeline: every request passes through these, in order ----
app.UseExceptionHandler();      // turn unexpected crashes into a clean 500 instead of leaking details
app.UseStatusCodePages();
app.UseRateLimiter();           // slow down abuse
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

app.Run();

// Lets the test project start the whole server in memory.
public partial class Program;
