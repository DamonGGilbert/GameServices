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
        string connection = configuration.GetConnectionString("Default") ?? "Data Source=gameservices.db";
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));

        // ---- Passwords: salted, slow, adjustable hashing (PBKDF2 via ASP.NET Core Identity) ----
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        // ---- Tokens ----
        JwtOptions jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        jwt.Validate();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<TokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep claim names exactly as we wrote them ("sub", "unique_name"). By default
                // ASP.NET renames "sub" to a long Microsoft URL, which would hide the user id.
                options.MapInboundClaims = false;

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
