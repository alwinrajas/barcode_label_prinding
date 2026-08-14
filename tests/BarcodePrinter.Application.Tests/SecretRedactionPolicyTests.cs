using BarcodePrinter.Api.Middleware;
using FluentAssertions;
using Serilog;
using Serilog.Events;
using Xunit;

namespace BarcodePrinter.Application.Tests;

/// <summary>
/// §13: no passwords or credentials in logs. Asserted through a real Serilog
/// pipeline rather than by calling the policy directly, because what matters is
/// what reaches a SINK — a policy that is correct but not wired up protects
/// nothing.
/// </summary>
public class SecretRedactionPolicyTests
{
    [Fact]
    public void A_destructured_login_request_never_carries_the_password_to_a_sink()
    {
        var events = Capture(log => log.Information(
            "Login attempt {@Request}",
            new FakeLoginRequest("ravi", "Sup3rSecret!", "WS-14")));

        var rendered = events.Single().RenderMessage();
        rendered.Should().NotContain("Sup3rSecret!");
        rendered.Should().Contain("***REDACTED***");

        // The non-secret fields are what make the log entry useful; redaction
        // must not blank the whole object.
        rendered.Should().Contain("ravi");
        rendered.Should().Contain("WS-14");
    }

    [Fact]
    public void Nested_objects_are_redacted_too()
    {
        var events = Capture(log => log.Information(
            "Config {@Settings}",
            new FakeSettings("Oracle", new FakeCredentials("scott", "tiger"))));

        events.Single().RenderMessage().Should().NotContain("tiger");
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("PasswordHash")]
    [InlineData("Pwd")]
    [InlineData("ConnectionString")]
    [InlineData("SigningKey")]
    [InlineData("SecurityStamp")]
    [InlineData("RefreshToken")]
    [InlineData("ApiKey")]
    [InlineData("Authorization")]
    public void Every_secret_shaped_name_is_treated_as_a_secret(string name) =>
        SecretRedactionPolicy.IsSecret(name).Should().BeTrue();

    [Theory]
    [InlineData("Username")]
    [InlineData("ProductCode")]
    [InlineData("Workstation")]
    [InlineData("JobNo")]
    [InlineData("LabelCount")]
    public void Ordinary_names_are_left_alone(string name) =>
        SecretRedactionPolicy.IsSecret(name).Should().BeFalse(
            "over-redacting makes logs useless and trains people to turn it off");

    private static List<LogEvent> Capture(Action<ILogger> write)
    {
        var events = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .Destructure.With<SecretRedactionPolicy>()
            .WriteTo.Sink(new CollectingSink(events))
            .CreateLogger();

        write(logger);
        return events;
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    // Named to sit under the BarcodePrinter.* namespace the policy scopes itself to.
    private sealed record FakeLoginRequest(string Username, string Password, string Workstation);
    private sealed record FakeSettings(string Name, FakeCredentials Credentials);
    private sealed record FakeCredentials(string Username, string Password);
}
