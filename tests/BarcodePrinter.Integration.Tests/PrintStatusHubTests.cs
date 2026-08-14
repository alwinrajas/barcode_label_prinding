using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Printing;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// Live print status (B-16). The value of this channel is entirely in the
/// transitions that happen AFTER the operator stops looking, so the test drives
/// a real job through the real dispatcher and asserts the pushes arrive —
/// asserting the broadcaster is called would not prove the hub is reachable,
/// authorized, or serialising a usable payload.
/// </summary>
[Collection("api")]
public class PrintStatusHubTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;
    private long _productId, _templateId, _printerId;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);
        (_productId, _templateId, _printerId) = await PrintScenario.EnsureHistoryAsync(_admin, fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_submitted_job_pushes_its_transitions_through_to_completion()
    {
        var received = new List<PrintJobDto>();
        var reachedTerminal = new TaskCompletionSource();

        await using var connection = await ConnectAsync(job =>
        {
            lock (received)
            {
                received.Add(job);
            }
            if (job.Status is "Completed" or "Failed")
            {
                reachedTerminal.TrySetResult();
            }
        });
        await connection.InvokeAsync("SubscribeToAll");

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, _printerId, "CONE", null, null, "750[D]",
            7100, 7104, 5, 1, "it-hub"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        await reachedTerminal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        List<PrintJobDto> forThisJob;
        lock (received)
        {
            forThisJob = received.Where(j => j.Id == created.JobId).ToList();
        }

        forThisJob.Should().NotBeEmpty("the dispatcher must announce what it is doing");
        forThisJob.Select(j => j.Status).Should().Contain("Completed");

        var final = forThisJob.Last();
        final.JobNo.Should().Be(created.JobNo);
        final.ProductCode.Should().NotBeNullOrEmpty("the push carries the full DTO, not just an id");
        final.LabelCount.Should().Be(5);
    }

    /// <summary>
    /// The per-job group is what the print screen uses to follow the one job it
    /// just submitted without hearing about everyone else's.
    ///
    /// Uses a CLIENT-dispatched printer so the job stays Queued until this test
    /// acts on it. Against a server-dispatched printer the file transport
    /// finishes before a subscription can be established, and the test would be
    /// racing the dispatcher rather than testing the group.
    /// </summary>
    [Fact]
    public async Task A_per_job_subscription_receives_that_job()
    {
        var clientPrinterId = await EnsureClientDispatchedPrinterAsync();

        var response = await _admin.PostAsJsonAsync(ApiRoutes.Print.Jobs, new PrintRequest(
            _productId, _templateId, clientPrinterId, "CONE", null, null, "750[D]",
            7200, 7202, 3, 1, "it-hub"));
        response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<PrintJobCreatedResponse>())!;

        var seen = new TaskCompletionSource<PrintJobDto>();
        await using var connection = await ConnectAsync(job =>
        {
            if (job.Id == created.JobId)
            {
                seen.TrySetResult(job);
            }
        });
        await connection.InvokeAsync("SubscribeToJob", created.JobId);

        // Cancel is only legal while Queued — which it is, because nothing on
        // the server dispatches to a client-dispatched printer.
        (await _admin.PostAsync(ApiRoutes.Print.Cancel(created.JobId), null))
            .EnsureSuccessStatusCode();

        var job = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        job.JobNo.Should().Be(created.JobNo);
        job.Status.Should().Be("Cancelled");
    }

    private async Task<long> EnsureClientDispatchedPrinterAsync()
    {
        var printers = await _admin.GetFromJsonAsync<List<PrinterDto>>(
            $"{ApiRoutes.Printers.Base}/?activeOnly=false");
        if (printers!.FirstOrDefault(p => p.Code == "IT-HUB-CLIENT") is { } existing)
        {
            return existing.Id;
        }

        var created = await _admin.PostAsJsonAsync(ApiRoutes.Printers.Base, new SavePrinterRequest(
            "IT-HUB-CLIENT", "Hub test client printer", null, "File", "Client",
            null, null, null, "it-hub-workstation", 203, "Zpl", false, true));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;
    }

    private sealed record IdResponse(long Id);

    /// <summary>A hub carries the same data as the endpoint behind it. If it
    /// were anonymous it would be a way round the permission (§13).</summary>
    [Fact]
    public async Task The_hub_refuses_an_unauthenticated_connection()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(fx.CreateClient().BaseAddress!, ApiRoutes.Print.Hub), o =>
            {
                o.HttpMessageHandlerFactory = _ => fx.Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var connect = async () => await connection.StartAsync();
        await connect.Should().ThrowAsync<Exception>("the hub requires Print.View");

        await connection.DisposeAsync();
    }

    private async Task<HubConnection> ConnectAsync(Action<PrintJobDto> onJobChanged)
    {
        var token = await GetTokenAsync("it-admin", ApiFixture.AdminPassword);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(fx.CreateClient().BaseAddress!, ApiRoutes.Print.Hub), o =>
            {
                // The in-memory test server has no real socket, so SignalR is
                // pointed at the WebApplicationFactory's handler.
                o.HttpMessageHandlerFactory = _ => fx.Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        connection.On("JobChanged", onJobChanged);
        await connection.StartAsync();
        return connection;
    }

    private async Task<string> GetTokenAsync(string username, string password)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fx.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetTokenAsync(username, password));
        return client;
    }
}
