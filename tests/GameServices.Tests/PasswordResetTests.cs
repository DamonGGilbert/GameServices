using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>An email "outbox" for tests: remembers every email instead of sending it.</summary>
public class CapturingEmailSender : IEmailSender
{
    public record Sent(string To, string Subject, string Body);

    private readonly List<Sent> sent = new();
    private readonly object gate = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken cancel)
    {
        lock (gate) sent.Add(new Sent(to, subject, body));
        return Task.CompletedTask;
    }

    public List<Sent> To(string email)
    {
        lock (gate) return sent.Where(s => string.Equals(s.To, email, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The 8-letter code in the newest email to this address (dash removed).</summary>
    public string LatestCode(string email)
    {
        Sent last = To(email).Last();
        Match match = Regex.Match(last.Body, "([A-Z2-9]{4})-([A-Z2-9]{4})");
        Assert.True(match.Success, "no code found in the email");
        return match.Groups[1].Value + match.Groups[2].Value;
    }
}

public class PasswordResetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    private CapturingEmailSender Outbox => factory.Services.GetRequiredService<CapturingEmailSender>();

    public PasswordResetTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private async Task<(string Username, string Email, AuthResponse Auth)> NewAccount()
    {
        HttpClient client = factory.CreateClient();
        string name = "r" + Guid.NewGuid().ToString("N")[..10];
        string email = name + "@example.com";

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "OldPassword1", email));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (name, email, (await response.Content.ReadFromJsonAsync<AuthResponse>())!);
    }

    private Task<HttpResponseMessage> Forgot(string email) => factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));

    private Task<HttpResponseMessage> Reset(string email, string code, string password) => factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(email, code, password));

    private Task<HttpResponseMessage> Login(string name, string password) => factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(name, password));

    [Fact]
    public async Task Asking_for_a_code_sends_one_email_with_a_code()
    {
        var account = await NewAccount();

        HttpResponseMessage response = await Forgot(account.Email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(Outbox.To(account.Email));
        Assert.Matches("^[A-Z2-9]{8}$", Outbox.LatestCode(account.Email));
    }

    [Fact]
    public async Task An_unknown_email_gets_the_same_answer_and_no_email()
    {
        var account = await NewAccount();

        string known = await (await Forgot(account.Email)).Content.ReadAsStringAsync();
        HttpResponseMessage unknown = await Forgot("nobody-" + Guid.NewGuid().ToString("N")[..8] + "@example.com");

        // Nobody can use this page to discover which emails have accounts.
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(known, await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_email_address_is_not_case_sensitive()
    {
        var account = await NewAccount();

        await Forgot(account.Email.ToUpperInvariant());

        Assert.Single(Outbox.To(account.Email));
    }

    [Fact]
    public async Task The_right_code_changes_the_password()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string code = Outbox.LatestCode(account.Email);

        Assert.Equal(HttpStatusCode.OK, (await Reset(account.Email, code, "BrandNewPass1")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Login(account.Username, "BrandNewPass1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(account.Username, "OldPassword1")).StatusCode);
    }

    [Fact]
    public async Task The_code_can_be_typed_with_a_dash_and_lower_case()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string code = Outbox.LatestCode(account.Email);
        string typed = (code[..4] + "-" + code[4..]).ToLowerInvariant();

        Assert.Equal(HttpStatusCode.OK, (await Reset(account.Email, typed, "BrandNewPass1")).StatusCode);
    }

    [Fact]
    public async Task A_code_only_works_once()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string code = Outbox.LatestCode(account.Email);

        await Reset(account.Email, code, "BrandNewPass1");
        HttpResponseMessage again = await Reset(account.Email, code, "AnotherPass22");

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(account.Username, "BrandNewPass1")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_changes_nothing()
    {
        var account = await NewAccount();
        await Forgot(account.Email);

        HttpResponseMessage response = await Reset(account.Email, "AAAAAAAA", "BrandNewPass1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(account.Username, "OldPassword1")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_guesses_cancel_the_code_even_for_the_right_one()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string realCode = Outbox.LatestCode(account.Email);

        for (int i = 0; i < PasswordResetEndpoints.MaxWrongGuesses; i++)
        {
            await Reset(account.Email, "BBBBBBBB", "BrandNewPass1");
        }

        HttpResponseMessage late = await Reset(account.Email, realCode, "BrandNewPass1");

        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(account.Username, "OldPassword1")).StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string code = Outbox.LatestCode(account.Email);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PasswordResetCodes.Where(c => c.UserId == account.Auth.User.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(account.Email, code, "BrandNewPass1")).StatusCode);
    }

    [Fact]
    public async Task A_code_for_one_account_does_not_work_on_another()
    {
        var alice = await NewAccount();
        var bob = await NewAccount();
        await Forgot(alice.Email);
        string alicesCode = Outbox.LatestCode(alice.Email);

        HttpResponseMessage attack = await Reset(bob.Email, alicesCode, "HackedPass123");

        Assert.Equal(HttpStatusCode.BadRequest, attack.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(bob.Username, "OldPassword1")).StatusCode);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_the_code_is_kept()
    {
        var account = await NewAccount();
        await Forgot(account.Email);
        string code = Outbox.LatestCode(account.Email);

        Assert.Equal(HttpStatusCode.BadRequest, (await Reset(account.Email, code, "short")).StatusCode);

        // The same code still works with a good password.
        Assert.Equal(HttpStatusCode.OK, (await Reset(account.Email, code, "BrandNewPass1")).StatusCode);
    }

    [Fact]
    public async Task Resetting_the_password_signs_the_account_out_everywhere()
    {
        var account = await NewAccount();
        await Forgot(account.Email);

        await Reset(account.Email, Outbox.LatestCode(account.Email), "BrandNewPass1");

        HttpResponseMessage refresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(account.Auth.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Asking_over_and_over_only_sends_three_emails_an_hour()
    {
        var account = await NewAccount();

        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Forgot(account.Email)).StatusCode);
        }

        Assert.Equal(PasswordResetEndpoints.MaxCodesPerHour, Outbox.To(account.Email).Count);
    }

    [Fact]
    public async Task The_admin_command_can_reset_a_forgotten_password()
    {
        var account = await NewAccount();

        using IServiceScope scope = factory.Services.CreateScope();
        bool done = await AdminCommands.ResetPassword(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<User>>(),
            account.Username, "AdminSetPass1");

        Assert.True(done);
        Assert.Equal(HttpStatusCode.OK, (await Login(account.Username, "AdminSetPass1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(account.Username, "OldPassword1")).StatusCode);
    }
}
