using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace BarcodePrinter.Api.Middleware;

/// <summary>
/// Redacts secret-looking properties when an object is destructured into a log
/// event (§13: "no passwords or credentials in logs").
///
/// The risk this removes is ordinary and easy to miss in review: someone writes
/// <c>logger.LogInformation("Login attempt {@Request}", request)</c> and the
/// password lands in a file that is kept for 30 days and copied off-box by the
/// nightly backup. Reviewers catch that most of the time. This catches it the
/// rest of the time.
///
/// Matching is by property NAME, deliberately broad, and applied before the
/// value is ever read — the point is that a secret cannot reach a sink even if
/// nobody was thinking about it at the call site.
/// </summary>
public sealed class SecretRedactionPolicy : IDestructuringPolicy
{
    private const string Redacted = "***REDACTED***";

    private static readonly string[] SecretNameFragments =
    [
        "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
        "connectionstring", "signingkey", "clientsecret", "credential",
        "securitystamp", "passwordhash", "authorization",
    ];

    public bool TryDestructure(
        object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue? result)
    {
        var type = value.GetType();

        // Only our own DTOs and domain objects. Framework and BCL types are left
        // to Serilog's normal handling, and destructuring them here would be a
        // large, pointless performance cost on every log call.
        if (!(type.FullName ?? "").StartsWith("BarcodePrinter.", StringComparison.Ordinal))
        {
            result = null;
            return false;
        }

        var properties = new List<LogEventProperty>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (IsSecret(property.Name))
            {
                properties.Add(new LogEventProperty(property.Name, new ScalarValue(Redacted)));
                continue;
            }

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch (Exception)
            {
                // A throwing getter must not take down the log call that was
                // trying to record what went wrong.
                continue;
            }

            properties.Add(new LogEventProperty(property.Name, factory.CreatePropertyValue(
                propertyValue, destructureObjects: true)));
        }

        result = new StructureValue(properties, type.Name);
        return true;
    }

    public static bool IsSecret(string propertyName) =>
        SecretNameFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
