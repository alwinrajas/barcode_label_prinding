using System.Net.Http;
using System.Text.Json;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Wpf.Features.Products;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Products screen behaviour: drawer dirty-state, free-text UOM resolution (what
/// actually reaches the wire), and the list states — including the search
/// truncation the server now reports honestly as HasMore with no cursor.
/// </summary>
public sealed class ProductsViewModelTests
{
    private static object Page(object[] items, string? nextCursor, bool hasMore) =>
        new { items, nextCursor, hasMore };

    private static object Summary(long id, string code, string description) => new
    {
        id, code, description, uom = "PCS", size = "M2", color = "NATURAL",
        defaultBatch = "CONE", isActive = true, hasImage = false, imageHash = (string?)null,
    };

    private static async Task<(ProductsViewModel Vm, RoutingHandler Handler)> CreateAsync(
        object? productsPage = null, object[]? uoms = null)
    {
        var handler = new RoutingHandler();
        handler.Route("/api/uoms", uoms ?? [new { id = 7L, code = "PCS", name = "Pieces" }]);
        handler.Route("/api/categories", Array.Empty<object>());
        handler.Route("/api/products", productsPage ?? Page([], null, false));

        var api = await handler.LoggedInClientAsync();
        var vm = new ProductsViewModel(new ProductsApi(api), TestSession.Create());
        await WaitForAsync(() => vm.HasLoadedOnce);
        return (vm, handler);
    }

    /// <summary>ViewModels kick their loads off the constructor, so tests wait
    /// for the state rather than assuming completion.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(20);
        }
        condition().Should().BeTrue("the awaited view-model state never arrived");
    }

    // ---- Drawer dirty state -------------------------------------------------

    [Fact]
    public void A_new_editor_starts_clean_and_becomes_dirty_on_the_first_edit()
    {
        var editor = ProductEditModel.CreateNew();
        editor.IsDirty.Should().BeFalse("nothing has been typed yet, so closing must not prompt");

        editor.Code = "NEW-01";
        editor.IsDirty.Should().BeTrue();

        editor.MarkClean();
        editor.IsDirty.Should().BeFalse("saving re-baselines the drawer");
    }

    [Fact]
    public void Reverting_an_edit_clears_the_dirty_flag()
    {
        var editor = ProductEditModel.CreateNew();
        editor.Description = "Cap";
        editor.IsDirty.Should().BeTrue();

        editor.Description = null;
        editor.IsDirty.Should().BeFalse("the drawer matches its baseline again");
    }

    // ---- Free-text UOM, verified on the wire --------------------------------

    [Fact]
    public async Task Saving_with_a_known_unit_sends_its_id_not_a_new_code()
    {
        var (vm, handler) = await CreateAsync();
        handler.RouteMethod(HttpMethod.Post, "/api/products", new { id = 99L }, System.Net.HttpStatusCode.Created);

        vm.NewProductCommand.Execute(null);
        vm.Editor!.Code = "P-1";
        vm.Editor.Description = "Product one";
        vm.Editor.UomText = "pcs";                       // matches seeded PCS, different case

        await vm.SaveCommand.ExecuteAsync(null);

        var body = handler.Requests.Last(r => r.Method == "POST" && r.Path.Contains("/api/products")).Body;
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("uomId").GetInt64().Should().Be(7,
            "an existing unit must reuse its row rather than create a duplicate");
        json.RootElement.GetProperty("uomCode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Saving_with_a_new_unit_sends_an_uppercased_code_for_the_server_to_create()
    {
        var (vm, handler) = await CreateAsync();
        handler.RouteMethod(HttpMethod.Post, "/api/products", new { id = 99L }, System.Net.HttpStatusCode.Created);

        vm.NewProductCommand.Execute(null);
        vm.Editor!.Code = "P-2";
        vm.Editor.Description = "Product two";
        vm.Editor.UomText = " itx9 ";

        await vm.SaveCommand.ExecuteAsync(null);

        var body = handler.Requests.Last(r => r.Method == "POST" && r.Path.Contains("/api/products")).Body;
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("uomCode").GetString().Should().Be("ITX9");
        json.RootElement.GetProperty("uomId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_over_long_unit_code_is_rejected_before_anything_is_sent()
    {
        var (vm, handler) = await CreateAsync();
        var postsBefore = handler.Requests.Count(r => r.Method == "POST");

        vm.NewProductCommand.Execute(null);
        vm.Editor!.Code = "P-3";
        vm.Editor.Description = "Product three";
        vm.Editor.UomText = new string('X', 17);

        await vm.SaveCommand.ExecuteAsync(null);

        vm.Editor.ErrorMessage.Should().Contain("16");
        handler.Requests.Count(r => r.Method == "POST").Should().Be(postsBefore,
            "client-side validation must not waste a round trip");
    }

    // ---- List states --------------------------------------------------------

    [Fact]
    public async Task A_capped_search_offers_a_refine_hint_instead_of_a_load_more_button()
    {
        // The server reports a truncated search as HasMore with NO cursor:
        // more matches exist, but relevance order cannot be keyset-paged.
        var (vm, _) = await CreateAsync(
            Page([Summary(1, "ITTRUNC1", "One")], nextCursor: null, hasMore: true));

        vm.ShowSearchTruncation.Should().BeTrue();
        vm.ShowLoadMore.Should().BeFalse("there is no cursor to follow");
        vm.SearchTruncationMessage.Should().Contain("Refine");
    }

    [Fact]
    public async Task A_browsable_page_offers_load_more()
    {
        var (vm, _) = await CreateAsync(
            Page([Summary(1, "A-1", "One")], nextCursor: "abc", hasMore: true));

        vm.ShowLoadMore.Should().BeTrue();
        vm.ShowSearchTruncation.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_catalogue_asks_the_user_to_add_the_first_product()
    {
        var (vm, _) = await CreateAsync(Page([], null, false));

        vm.ShowNoProductsState.Should().BeTrue();
        vm.ShowNoMatchesState.Should().BeFalse();
        vm.ShowInitialBusy.Should().BeFalse("the first response has landed");
    }

    [Fact]
    public async Task A_fruitless_search_names_the_term_that_found_nothing()
    {
        var (vm, _) = await CreateAsync(Page([], null, false));

        vm.SearchText = "nothing-matches-this";
        await WaitForAsync(() => vm.ShowNoMatchesState);

        vm.NoMatchesMessage.Should().Contain("nothing-matches-this");
        vm.ShowNoProductsState.Should().BeFalse("this is a search miss, not an empty catalogue");
    }

    [Fact]
    public async Task A_failed_list_load_surfaces_an_actionable_error_with_its_reference()
    {
        var handler = new RoutingHandler();
        handler.Route("/api/uoms", Array.Empty<object>());
        handler.Route("/api/products", new
        {
            status = 500, title = "UNEXPECTED", detail = "Unable to load products.",
            code = "UNEXPECTED", correlationId = "BP-7F42A",
        }, System.Net.HttpStatusCode.InternalServerError);

        var api = await handler.LoggedInClientAsync();
        var vm = new ProductsViewModel(new ProductsApi(api), TestSession.Create());
        await WaitForAsync(() => vm.HasLoadedOnce);

        vm.ShowErrorState.Should().BeTrue(
            "requests seen: {0}; message={1}",
            string.Join(" | ", handler.Requests.Select(r => $"{r.Method} {r.Path}")),
            vm.ListErrorMessage ?? "<null>");
        vm.ListErrorReference.Should().Be("BP-7F42A", "support needs the correlation id");
        vm.ListErrorMessage.Should().NotBeNullOrEmpty();
    }
}
