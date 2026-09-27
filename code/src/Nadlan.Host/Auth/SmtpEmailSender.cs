using System.Net;
using System.Net.Mail;
using Nadlan.Core.Security;
using Nadlan.Host.Configuration;

namespace Nadlan.Host.Auth;

/// <summary>Plain SMTP (works with Amazon SES SMTP, Gmail app passwords, ...). Configured in Nadlan:Auth:Email.</summary>
internal sealed class SmtpEmailSender : IEmailSender
{
    private readonly NadlanOptions.EmailOptions _options;

    public SmtpEmailSender(NadlanOptions.EmailOptions options)
    {
        _options = options;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.SmtpHost) && !string.IsNullOrWhiteSpace(_options.From);

    public async Task SendAsync(string to, string subject, string textBody, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Email is not configured (Nadlan:Auth:Email:SmtpHost / From).");
        }

        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort) { EnableSsl = _options.EnableSsl };
        if (!string.IsNullOrWhiteSpace(_options.SmtpUser))
        {
            client.Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword);
        }

        using var message = new MailMessage(_options.From, to, subject, textBody);
        await client.SendMailAsync(message, ct);
    }
}
