using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Dashboard;

namespace BarcodePrinter.Client.Core;

public sealed class DashboardApi(ApiClient api)
{
    public Task<DashboardDto> GetAsync(CancellationToken ct) =>
        api.GetAsync<DashboardDto>(ApiRoutes.Dashboard.Base, ct);
}
