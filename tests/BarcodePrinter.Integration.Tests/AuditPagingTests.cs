using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Products;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

/// <summary>
/// The audit log is evidence (§13/§14). A pager that quietly skips entries makes
/// it evidence of nothing, so the same composite-cursor property proved for
/// print history is proved here.
/// </summary>
[Collection("api")]
public class AuditPagingTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = await LoginAsync();

        // Generate enough auditable activity to page through several times.
        for (var i = 0; i < 45; i++)
        {
            await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, new SaveProductRequest(
                $"IT-AUDIT-{i:00}", $"Audit paging {i}", null, null, null,
                null, null, null, null, null, null));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Paging_the_audit_log_visits_each_entry_exactly_once()
    {
        const int pageSize = 10;
        var seen = new HashSet<long>();
        var duplicates = new List<long>();
        string? cursor = null;
        var previous = DateTime.MaxValue;

        for (var page = 0; page < 4; page++)
        {
            var url = $"{ApiRoutes.Audit.Base}?pageSize={pageSize}" +
                      (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var result = (await _admin.GetFromJsonAsync<PagedResult<AuditEntryDto>>(url))!;

            result.Items.Should().NotBeEmpty($"page {page} should be full");
            foreach (var entry in result.Items)
            {
                if (!seen.Add(entry.Id))
                {
                    duplicates.Add(entry.Id);
                }
                entry.OccurredAtUtc.Should().BeOnOrBefore(previous,
                    "the walk must stay in descending time order across page boundaries");
                previous = entry.OccurredAtUtc;
            }

            cursor = result.NextCursor;
            cursor.Should().NotBeNull("this suite alone writes more than four pages of audit entries");
        }

        duplicates.Should().BeEmpty("an audit pager must never re-serve an entry");
        seen.Should().HaveCount(4 * pageSize, "and must never skip one");
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest("it-admin", ApiFixture.AdminPassword, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }
}
