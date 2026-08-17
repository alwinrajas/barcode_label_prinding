using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Auth;
using BarcodePrinter.Contracts.Products;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace BarcodePrinter.Integration.Tests;

[Collection("api")]
public class ProductApiTests(ApiFixture fx) : IAsyncLifetime
{
    private HttpClient _admin = null!;

    public async Task InitializeAsync() =>
        _admin = await LoginAsync("it-admin", ApiFixture.AdminPassword);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- CRUD ----------------------------------------------------------------

    [Fact]
    public async Task Create_read_update_deactivate_roundtrip()
    {
        var create = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base,
            Request("IT-CRUD-01", "Crud test product"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        var detail = (await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(id)))!;
        detail.Code.Should().Be("IT-CRUD-01");
        detail.DefaultBatch.Should().Be("BATCH-1");
        detail.IsActive.Should().BeTrue();

        var update = await _admin.PutAsJsonAsync(ApiRoutes.Products.ById(id),
            Request("IT-CRUD-01", "Renamed product", detail.ConcurrencyStamp));
        update.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var delete = await _admin.DeleteAsync(ApiRoutes.Products.ById(id));
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = (await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(id)))!;
        after.Description.Should().Be("Renamed product");
        after.IsActive.Should().BeFalse("delete means deactivate — history references products forever");
    }

    [Fact]
    public async Task Duplicate_code_is_rejected_with_stable_error()
    {
        (await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, Request("IT-DUP-01", "First")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var dup = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, Request("IT-DUP-01", "Second"));
        dup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await dup.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ProductCodeDuplicate);
    }

    [Fact]
    public async Task Stale_concurrency_stamp_returns_409_not_silent_overwrite()
    {
        var create = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base,
            Request("IT-CONC-01", "Concurrency test"));
        var id = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        var detail = (await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(id)))!;

        // First editor saves fine.
        (await _admin.PutAsJsonAsync(ApiRoutes.Products.ById(id),
            Request("IT-CONC-01", "Editor A", detail.ConcurrencyStamp)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Second editor still holds the OLD stamp.
        var conflicted = await _admin.PutAsJsonAsync(ApiRoutes.Products.ById(id),
            Request("IT-CONC-01", "Editor B", detail.ConcurrencyStamp));
        conflicted.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conflicted.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ConcurrencyConflict);
    }

    // ---- RBAC on products ------------------------------------------------------

    [Fact]
    public async Task User_role_can_view_but_not_create_products()
    {
        var user = await LoginAsync("it-user", ApiFixture.UserPassword);

        (await user.GetAsync($"{ApiRoutes.Products.Base}/?pageSize=1"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await user.PostAsJsonAsync(ApiRoutes.Products.Base, Request("IT-RBAC-01", "Nope")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Search (§9.3) -----------------------------------------------------------

    [Fact]
    public async Task Search_finds_substring_via_ngram_and_ranks_exact_code_first()
    {
        await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, Request("5GCAPM2N", "5G M2 CAP"));
        await _admin.PostAsJsonAsync(ApiRoutes.Products.Base, Request("5GCAPM3NOSW", "5G M3 CAP"));

        // Substring from the MIDDLE of the code — the LIKE-prefix path cannot
        // find this; only the ngram FULLTEXT can.
        var mid = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=CAPM2");
        mid!.Items.Should().Contain(p => p.Code == "5GCAPM2N");

        // Exact code always ranks first.
        var exact = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=5GCAPM3NOSW");
        exact!.Items.Should().NotBeEmpty();
        exact.Items[0].Code.Should().Be("5GCAPM3NOSW");
    }

    [Fact]
    public async Task Keyset_pagination_walks_without_overlap()
    {
        for (var i = 0; i < 5; i++)
        {
            await _admin.PostAsJsonAsync(ApiRoutes.Products.Base,
                Request($"IT-PAGE-{i:00}", $"Page test {i}"));
        }

        var page1 = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=&pageSize=2");
        page1!.Items.Should().HaveCount(2);
        page1.HasMore.Should().BeTrue();

        var page2 = await _admin.GetFromJsonAsync<PagedResult<ProductSummary>>(
            $"{ApiRoutes.Products.Base}/?q=&pageSize=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        page2!.Items.Select(i => i.Id).Should().NotIntersectWith(page1.Items.Select(i => i.Id));
    }

    // ---- Images (§9.4) --------------------------------------------------------------

    [Fact]
    public async Task Image_upload_serves_full_and_thumb_with_etag_304()
    {
        var create = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base,
            Request("IT-IMG-01", "Image test"));
        var id = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        using var content = new MultipartFormDataContent();
        var png = new ByteArrayContent(MakeTestPng(800, 600));
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(png, "file", "sample.png");

        var upload = await _admin.PostAsync(ApiRoutes.Products.Image(id), content);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        // Full variant is a JPEG (re-encoded server-side — EXIF stripped).
        var full = await _admin.GetAsync(ApiRoutes.Products.Image(id));
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        full.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        var etag = full.Headers.ETag!.Tag;

        // Thumb variant exists and is smaller.
        var thumb = await _admin.GetAsync($"{ApiRoutes.Products.Image(id)}?variant=thumb");
        thumb.StatusCode.Should().Be(HttpStatusCode.OK);
        (await thumb.Content.ReadAsByteArrayAsync()).Length
            .Should().BeLessThan((await full.Content.ReadAsByteArrayAsync()).Length);

        // Cache round-trip: If-None-Match → 304, no body.
        var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Products.Image(id));
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        (await _admin.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        // Grid row reports the image.
        var detail = (await _admin.GetFromJsonAsync<ProductDetail>(ApiRoutes.Products.ById(id)))!;
        detail.HasImage.Should().BeTrue();
        detail.ImageHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Non_image_upload_is_rejected()
    {
        var create = await _admin.PostAsJsonAsync(ApiRoutes.Products.Base,
            Request("IT-IMG-02", "Bad image test"));
        var id = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;

        using var content = new MultipartFormDataContent();
        var fake = new ByteArrayContent("MZ this is not an image"u8.ToArray());
        fake.Headers.ContentType = new MediaTypeHeaderValue("image/png");   // lying mime
        content.Add(fake, "file", "notanimage.png");

        var upload = await _admin.PostAsync(ApiRoutes.Products.Image(id), content);
        upload.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "content is validated by decoding, not by trusting the extension or mime");
    }

    // ---- Performance exit criterion (10k rows, search < 150 ms) -------------------

    [Fact]
    public async Task Search_p95_under_150ms_with_10k_products()
    {
        await Seed10kAsync();

        // Warm up connections and the buffer pool.
        for (var i = 0; i < 5; i++)
        {
            await _admin.GetAsync($"{ApiRoutes.Products.Base}/?q=PERF12");
        }

        var samples = new List<double>();
        string[] terms = ["PERF12", "PERF87", "widget", "PERF-00042", "RF999", "PERF55"];
        for (var i = 0; i < 30; i++)
        {
            var sw = Stopwatch.StartNew();
            var response = await _admin.GetAsync($"{ApiRoutes.Products.Base}/?q={terms[i % terms.Length]}");
            sw.Stop();
            response.EnsureSuccessStatusCode();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p95 = samples[(int)(samples.Count * 0.95) - 1];
        p95.Should().BeLessThan(150,
            $"§11.1 exit criterion. Samples(ms): min={samples[0]:F1} p50={samples[samples.Count / 2]:F1} p95={p95:F1}");
    }

    private async Task Seed10kAsync()
    {
        await using var conn = await fx.OpenDbAsync();
        var already = await new MySqlConnector.MySqlCommand(
            "SELECT COUNT(*) FROM products WHERE code LIKE 'PERF-%'", conn).ExecuteScalarAsync();
        if (Convert.ToInt64(already) >= 10_000)
        {
            return;
        }

        // Multi-value inserts, 1k per statement — seeding infrastructure, not
        // the import pipeline (that arrives in phase 4 with MySqlBulkCopy).
        for (var batch = 0; batch < 10; batch++)
        {
            var sb = new System.Text.StringBuilder(
                "INSERT IGNORE INTO products (code, description, concurrency_stamp, created_at) VALUES ");
            for (var i = 0; i < 1_000; i++)
            {
                var n = batch * 1_000 + i;
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append($"('PERF-{n:00000}','Performance widget {n} type {(char)('A' + n % 26)}',UUID(),UTC_TIMESTAMP(3))");
            }
            await new MySqlConnector.MySqlCommand(sb.ToString(), conn) { CommandTimeout = 120 }
                .ExecuteNonQueryAsync();
        }
    }

    private static byte[] MakeTestPng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.DarkSlateBlue };
            canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 3f, paint);
        }
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SaveProductRequest Request(string code, string description, string? stamp = null) => new(
        code, description, null, "M2", "NATURAL",
        "BATCH-1", 750, "750[D]", 750, 10, stamp);

    private sealed record CreatedResponse(long Id);

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = fx.CreateClient();
        var response = await client.PostAsJsonAsync(ApiRoutes.Auth.Login,
            new LoginRequest(username, password, "it-tests"));
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return client;
    }
}
