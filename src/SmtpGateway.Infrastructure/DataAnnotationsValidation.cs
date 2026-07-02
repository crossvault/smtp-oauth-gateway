using System.ComponentModel.DataAnnotations;

namespace SmtpGateway.Infrastructure;

/// <summary>
/// Shared DataAnnotations validation helper used by both <see cref="GatewayOptionsValidator"/>
/// and <see cref="OutboundProviderFactory"/>: validates a single settings object and throws one
/// exception listing every violated rule (not just the first), so a misconfigured
/// appsettings.json can be fixed in one pass.
/// </summary>
internal static class DataAnnotationsValidation
{
    public static void Validate(object instance, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var context = new ValidationContext(instance);
        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(instance, context, results, validateAllProperties: true))
        {
            var errors = string.Join("; ", results.Select(r => r.ErrorMessage));
            throw new InvalidOperationException($"Configuration section '{sectionName}' is invalid: {errors}");
        }
    }
}
