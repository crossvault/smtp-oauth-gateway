using SmtpGateway.Core;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Builds the single, actively configured <see cref="IOutboundProvider"/> from
/// <see cref="OutboundProviderOptions"/>. Exactly one provider is ever active in the MVP - no
/// domain/sender/fallback routing rules.
/// </summary>
public static class OutboundProviderFactory
{
    private const string M365SmtpHost = "smtp.office365.com";
    private const int M365SmtpPort = 587;
    private const string M365SmtpScope = "https://outlook.office365.com/.default";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    public static IOutboundProvider Create(OutboundProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ParseKind(options.Provider) switch
        {
            OutboundProviderKind.GenericSmtp => CreateGenericSmtp(RequireSection(options.GenericSmtp, "GenericSmtp")),
            OutboundProviderKind.M365Oauth => CreateM365Oauth(RequireSection(options.M365Oauth, "M365Oauth")),
            OutboundProviderKind.Graph => CreateGraph(RequireSection(options.Graph, "Graph")),
            var kind => throw new InvalidOperationException($"Unhandled outbound provider kind '{kind}'."),
        };
    }

    private static OutboundProviderKind ParseKind(string provider)
    {
        if (Enum.TryParse<OutboundProviderKind>(provider, ignoreCase: true, out var kind)
            && Enum.IsDefined(kind))
        {
            return kind;
        }

        throw new InvalidOperationException(
            $"Unknown outbound provider '{provider}'. Valid values are: " +
            string.Join(", ", Enum.GetNames<OutboundProviderKind>()) + ".");
    }

    private static T RequireSection<T>(T? section, string sectionName) where T : class
    {
        if (section is null)
        {
            throw new InvalidOperationException(
                $"Outbound provider '{sectionName}' is selected but its configuration section is missing.");
        }

        DataAnnotationsValidation.Validate(section, sectionName);
        return section;
    }

    private static IOutboundProvider CreateGenericSmtp(GenericSmtpSettings settings) =>
        new GenericSmtpProvider(new GenericSmtpProviderOptions
        {
            Host = settings.Host!,
            Port = settings.Port,
            TlsMode = settings.TlsMode,
            AuthMode = settings.AuthMode,
            Username = settings.Username,
            Password = settings.Password,
            TrustAllCertificates = settings.TrustAllCertificates,
        });

    private static IOutboundProvider CreateM365Oauth(M365OauthSettings settings)
    {
        var tokenProvider = new MsalTokenProvider(settings.TenantId!, settings.ClientId!, settings.ClientSecret!, M365SmtpScope);

        return new GenericSmtpProvider(new GenericSmtpProviderOptions
        {
            Host = M365SmtpHost,
            Port = M365SmtpPort,
            TlsMode = SmtpTlsMode.StartTlsRequired,
            AuthMode = AuthMode.M365Oauth,
            Username = settings.Mailbox,
            TokenProvider = tokenProvider,
        });
    }

    private static IOutboundProvider CreateGraph(GraphSettings settings)
    {
        var tokenProvider = new MsalTokenProvider(settings.TenantId!, settings.ClientId!, settings.ClientSecret!, GraphScope);

        return new GraphSendMailProvider(new GraphSendMailProviderOptions
        {
            Mailbox = settings.Mailbox!,
            TokenProvider = tokenProvider,
        });
    }
}
