using System.Text;
using System.Threading.RateLimiting;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace GameServices.Api.Infrastructure;

/// <summary>
/// Everything the server needs is registered here ("dependency injection").
/// Program.cs stays tiny, and each feature adds its own services in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameServices(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Database ----
        // Database:Provider picks the engine. "Sqlite" (default) is a single file, perfect on your own PC.
        // "Postgres" is the real database server used in Docker and when the game is hosted online.
        string provider = configuration["Database:Provider"] ?? "Sqlite";
        string connection = configuration.GetConnectionString("Default") ?? "Data Source=gameservices.db";

        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            // The rest of the code only knows "AppDbContext"; here we hand it the PostgreSQL flavour.
            services.AddDbContext<AppDbContext, PostgresAppDbContext>(options => options.UseNpgsql(connection));
        }
        else
        {
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        }

        // ---- Passwords: salted, slow, adjustable hashing (PBKDF2 via ASP.NET Core Identity) ----
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        // ---- Email: a real mail server if one is configured, otherwise the server window ----
        services.Configure<GameServices.Api.Email.EmailOptions>(configuration.GetSection(GameServices.Api.Email.EmailOptions.SectionName));

        if (!string.IsNullOrWhiteSpace(configuration[GameServices.Api.Email.EmailOptions.SectionName + ":Host"]))
        {
            services.AddSingleton<GameServices.Api.Email.IEmailSender, GameServices.Api.Email.SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<GameServices.Api.Email.IEmailSender, GameServices.Api.Email.LogEmailSender>();
        }

        // ---- Online duels ----
        services.Configure<GameServices.Api.Online.OnlineOptions>(configuration.GetSection(GameServices.Api.Online.OnlineOptions.SectionName));
        services.AddSingleton<GameServices.Api.Online.RoomRegistry>();
        services.AddSingleton<GameServices.Api.Online.DuelInviteRegistry>();
        services.AddSingleton<GameServices.Api.Online.IHostLauncher, GameServices.Api.Online.ProcessHostLauncher>();
        services.AddSingleton<GameServices.Api.Online.MatchCoordinator>();
        services.AddScoped<GameServices.Api.Online.MatchResults>();
        services.AddHostedService<GameServices.Api.Online.RoomSweeperService>();

        // ---- Tokens ----
        JwtOptions jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        // Development only: no signing key is stored in the code (this repository is public).
        // If none is configured, make a random one for this run. Players just refresh their
        // sign-in after a restart. Production MUST provide Jwt__Key, or the server refuses to start.
        if (string.IsNullOrWhiteSpace(jwt.Key) && string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase))
        {
            jwt.Key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        }

        jwt.Validate();

        services.Configure<JwtOptions>(options =>
        {
            configuration.GetSection(JwtOptions.SectionName).Bind(options);
            options.Key = jwt.Key;
        });
        services.AddScoped<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep claim names exactly as we wrote them ("sub", "unique_name"). By default
                // ASP.NET renames "sub" to a long Microsoft URL, which would hide the user id.
                options.MapInboundClaims = false;

                // Browsers and game WebSockets can't set an Authorization header, so for the chat
                // hub the access token travels in the URL (?access_token=...) instead.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        string? token = context.Request.Query["access_token"];

                        if (!string.IsNullOrEmpty(token) && (context.HttpContext.Request.Path.StartsWithSegments("/hubs") || context.HttpContext.Request.Path.StartsWithSegments("/ws")))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    }
                };

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        services.AddAuthorization();

        // ---- Rate limiting: a few attempts per minute per IP on the sign-in endpoints ----
        int permits = configuration.GetValue("RateLimit:AuthRequestsPerMinute", 20);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy("auth", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        // ---- API documentation ----
        services.AddOpenApi();
        services.AddProblemDetails();

        return services;
    }
}
