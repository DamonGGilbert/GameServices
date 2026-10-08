using System.Net;
using System.Net.Mail;

namespace GameServices.Api.Email;

/// <summary>
/// "Send this email." The rest of the server only knows this interface, so where the emails
/// really go (a log, a real mail service, a test) can be swapped without touching any feature.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancel);
}

/// <summary>
/// Used when no mail service is configured (your own PC): the "email" is printed in the server
/// window instead, so you can read the reset code there while developing.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancel)
    {
        logger.LogWarning("[DEV EMAIL - not really sent] To: {To} | Subject: {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}

public class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>The mail server, e.g. smtp-relay.brevo.com. Empty = just log emails.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";

    /// <summary>A SECRET. Set it with the Email__Password environment variable, never in a file.</summary>
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Duel Game";
}

/// <summary>Sends real email through any SMTP service (Brevo, SendGrid, Gmail, ...).</summary>
public class SmtpEmailSender(Microsoft.Extensions.Options.IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancel)
    {
        EmailOptions settings = options.Value;

        using SmtpClient client = new(settings.Host, settings.Port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(settings.Username, settings.Password)
        };

        using MailMessage message = new()
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };

        message.To.Add(to);

        try
        {
            await client.SendMailAsync(message, cancel);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException)
        {
            // The player is never told whether an email exists, so a failure is only logged.
            logger.LogError(ex, "Could not send an email to {To}.", to);
        }
    }
}
