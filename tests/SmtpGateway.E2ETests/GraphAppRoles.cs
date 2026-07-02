using System.Text.Json;

namespace SmtpGateway.E2ETests;

/// <summary>
/// Reads the application role values (the <c>roles</c> claim) from a Graph JWT access token so a
/// live test can gate itself on whether the sandbox app actually holds the Graph application
/// permission that a given product code path needs. The token is auth material: it is only ever
/// inspected in-memory here and is never logged, printed, or placed into an assertion message.
/// </summary>
internal static class GraphAppRoles
{
    public static IReadOnlySet<string> Read(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (document.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
            {
                return roles.EnumerateArray()
                    .Select(role => role.GetString())
                    .Where(role => !string.IsNullOrEmpty(role))
                    .Select(role => role!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // An opaque/non-JWT token simply yields "no observable roles" - the caller then skips.
        }
        catch (FormatException)
        {
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };
        return Convert.FromBase64String(normalized);
    }
}
