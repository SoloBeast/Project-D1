using System.Net;
using System.Net.Mail;
using DoodhDirect.Application.Integrations;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.Integrations;

/// <summary>
/// Delivers email through the SMTP relay configured in the INTEGRATION settings.
/// Settings are resolved at call time so a saved configuration takes effect on the
/// next send without a restart. Uses the built-in System.Net.Mail.SmtpClient.
///
/// When SMTP is not configured the send is skipped (returns
/// <see cref="EmailSendResult.Sent"/> = false) instead of throwing, so callers can
/// degrade gracefully. Transport failures throw and are handled by the caller.
/// </summary>
public sealed class SmtpEmailSender(
    IIntegrationSettingsProvider settingsProvider,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = await settingsProvider.GetEmailAsync(cancellationToken);
        if (!settings.IsConfigured
            || string.IsNullOrWhiteSpace(settings.Host)
            || string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            logger.LogInformation(
                "Email to {ToAddress} was skipped because SMTP delivery is not configured.",
                message.ToAddress);
            return new EmailSendResult(false, "SMTP delivery is not configured.");
        }

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseSsl,
            Timeout = (int)SendTimeout.TotalMilliseconds
        };

        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            client.Credentials = new NetworkCredential(
                settings.UserName,
                settings.Password ?? string.Empty);
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(settings.FromAddress),
            Subject = message.Subject,
            Body = message.PlainTextBody,
            IsBodyHtml = !string.IsNullOrWhiteSpace(message.HtmlBody)
        };

        if (!string.IsNullOrWhiteSpace(settings.FromName))
        {
            mail.From = new MailAddress(settings.FromAddress, settings.FromName);
        }

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            mail.Body = message.HtmlBody!;
        }

        mail.To.Add(message.ToAddress);

        await client.SendMailAsync(mail, cancellationToken);

        logger.LogInformation(
            "Email sent to {ToAddress} from {FromAddress} via SMTP host {Host}:{Port}.",
            message.ToAddress,
            settings.FromAddress,
            settings.Host,
            settings.Port);

        return new EmailSendResult(true, null);
    }
}
