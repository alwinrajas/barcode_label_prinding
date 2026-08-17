using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Wpf.Features.Printing;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// Print screen behaviour that the operator depends on: production/expiry date
/// defaults (and when the app must stop deriving them), and the disabled-print
/// explanation that replaced a silently dead button.
/// </summary>
public sealed class PrintViewModelTests
{
    private static async Task<(PrintViewModel Vm, RoutingHandler Handler)> CreateAsync(
        object? printers = null, object? productDetail = null)
    {
        var handler = new RoutingHandler();
        handler.Route("/api/printers", printers ?? new[]
        {
            new
            {
                id = 1L, code = "ZT230", name = "Zebra ZT230", location = (string?)null,
                connectionType = "NetworkTcp", dispatchMode = "Server", host = "192.168.1.50",
                port = 9100, windowsPrinterName = (string?)null, ownerWorkstation = (string?)null,
                dpi = 203, language = "Zpl", supportsStatusQuery = false,
                isActive = true, isDefault = true, lastSeenUtc = (DateTime?)null,
            },
        });
        handler.Route("/api/printers/1/status", new
        {
            printerId = 1L, online = true, detail = (string?)null, lastSeenUtc = DateTime.UtcNow,
        });
        handler.Route("/api/products/", productDetail ?? Detail());
        handler.Route("/api/print/preview", new
        {
            pngBase64 = (string?)null, zpl = "^XA^XZ", format = "Zpl",
            unavailable = "no preview in tests", warning = (string?)null,
        });

        var api = await handler.LoggedInClientAsync();
        var vm = new PrintViewModel(new PrintApi(api), new ProductsApi(api), TestSession.Create());
        return (vm, handler);
    }

    /// <summary>A product carrying no master date defaults — the case where the
    /// application must supply today / today + 1 year.</summary>
    private static object Detail(string? defaultProduction = null, string? defaultExpiry = null) => new
    {
        id = 5L, code = "5GCAPM2N", description = "5G M2 CAP", barcodeValue = (string?)null,
        uomId = (long?)null, uom = "PCS", size = "M2", color = "NATURAL",
        categoryId = (long?)null, category = (string?)null, defaultBatch = "CONE",
        defaultProductionDate = defaultProduction, defaultExpiryDate = defaultExpiry,
        defaultQuantity = 750m, defaultQuantityText = "750[D]", cartonQuantity = 750m,
        cartonsPerPallet = 10, isActive = true, hasImage = false, imageHash = (string?)null,
        concurrencyStamp = "stamp", createdAtUtc = DateTime.UtcNow, updatedAtUtc = (DateTime?)null,
    };

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

    private static ProductSummary Summary() =>
        new(5, "5GCAPM2N", "5G M2 CAP", "PCS", "M2", "NATURAL", "CONE", true, false, null);

    // ---- Date defaults ------------------------------------------------------

    [Fact]
    public async Task A_product_without_master_dates_defaults_to_today_and_one_year_on()
    {
        var (vm, _) = await CreateAsync();

        vm.SelectedProduct = Summary();
        await WaitForAsync(() => vm.ProductDetail is not null);

        vm.ProductionDate.Should().Be(DateTime.Today);
        vm.ExpiryDate.Should().Be(DateTime.Today.AddYears(1),
            "expiry defaults to exactly one year after production");
    }

    [Fact]
    public async Task Master_dates_win_over_the_computed_defaults()
    {
        var (vm, _) = await CreateAsync(
            productDetail: Detail(defaultProduction: "2026-07-21", defaultExpiry: "2028-01-31"));

        vm.SelectedProduct = Summary();
        await WaitForAsync(() => vm.ProductDetail is not null);

        vm.ProductionDate.Should().Be(new DateTime(2026, 7, 21));
        vm.ExpiryDate.Should().Be(new DateTime(2028, 1, 31),
            "the product's own expiry is not overwritten by the +1 year rule");
    }

    [Fact]
    public async Task Changing_production_re_derives_expiry()
    {
        var (vm, _) = await CreateAsync();
        vm.SelectedProduct = Summary();
        await WaitForAsync(() => vm.ProductDetail is not null);

        vm.ProductionDate = new DateTime(2027, 3, 1);

        vm.ExpiryDate.Should().Be(new DateTime(2028, 3, 1));
    }

    [Fact]
    public async Task An_operator_edited_expiry_is_never_overwritten()
    {
        var (vm, _) = await CreateAsync();
        vm.SelectedProduct = Summary();
        await WaitForAsync(() => vm.ProductDetail is not null);

        vm.ExpiryDate = new DateTime(2030, 12, 25);      // explicit operator override
        vm.ProductionDate = new DateTime(2027, 3, 1);    // must NOT re-derive now

        vm.ExpiryDate.Should().Be(new DateTime(2030, 12, 25),
            "once the operator sets an expiry, the application stops deriving it");
    }

    [Fact]
    public async Task Selecting_a_different_product_resumes_automatic_expiry()
    {
        var (vm, _) = await CreateAsync();
        vm.SelectedProduct = Summary();
        await WaitForAsync(() => vm.ProductDetail is not null);
        vm.ExpiryDate = new DateTime(2030, 12, 25);

        vm.ProductDetail = null;
        vm.SelectedProduct = new ProductSummary(
            6, "OTHER", "Other product", "PCS", "M1", "WHITE", "CONE", true, false, null);
        await WaitForAsync(() => vm.ProductDetail is not null);

        vm.ExpiryDate.Should().Be(DateTime.Today.AddYears(1),
            "a new product starts from the master defaults again");
    }

    // ---- Printer selection and the print gate -------------------------------

    [Fact]
    public async Task The_default_printer_is_selected_without_operator_action()
    {
        var (vm, _) = await CreateAsync();

        await WaitForAsync(() => vm.SelectedPrinter is not null);
        vm.SelectedPrinter!.Name.Should().Be("Zebra ZT230");
        vm.SelectedPrinter.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Print_is_blocked_with_a_stated_reason_until_a_product_is_chosen()
    {
        var (vm, _) = await CreateAsync();
        await WaitForAsync(() => vm.SelectedPrinter is not null);

        vm.PrintCommand.CanExecute(null).Should().BeFalse();
        vm.PrintDisabledReason.Should().NotBeNullOrEmpty(
            "a disabled Print button must explain itself rather than look broken");
        vm.PrintDisabledReason.Should().Contain("product");
    }

    [Fact]
    public async Task With_no_active_printer_the_screen_explains_instead_of_failing_silently()
    {
        var (vm, _) = await CreateAsync(printers: Array.Empty<object>());

        await WaitForAsync(() => vm.HasInitError);
        vm.InitErrorMessage.Should().NotBeNullOrEmpty();
        vm.PrintCommand.CanExecute(null).Should().BeFalse();
    }
}
