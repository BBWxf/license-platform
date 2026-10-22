using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LicensePlatform.Web.Pages.Admin.Batches;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class BatchExportPageTests
{
    private const string UnavailableMessage = "导出文件不可用。旧版已清除的数据无法还原；如存档仍在，请检查解密配置。";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly string ExportPath = "/Admin/Batches?handler=Export&id=" + AdminPageFixtureData.BatchId.ToString("D");

    [Fact]
    public async Task Actual_razor_get_downloads_repeat_without_antiforgery_and_keep_the_export_link()
    {
        var service = new BatchServiceSpy(available: true);
        await using var host = await AdminPageTestHost.StartAsync(batchService: service);
        await host.AuthenticateAsync();
        await AssertExportLinkAsync(host);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ExportPath);
            if (attempt == 1)
            {
                // FileContentResult does not opt into range or conditional ETag handling.
                request.Headers.TryAddWithoutValidation("Range", "bytes=0-3");
                request.Headers.TryAddWithoutValidation("If-None-Match", "\"fixture-public-etag\"");
            }

            using var response = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
            var disposition = response.Content.Headers.ContentDisposition;
            Assert.NotNull(disposition);
            Assert.Equal("attachment", disposition.DispositionType);
            Assert.Equal(service.FileName, disposition.FileNameStar ?? disposition.FileName?.Trim('"'));
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.AsSpan().SequenceEqual(service.Content), "The complete synthetic export must be returned unchanged on every request.");
            Assert.False(response.Headers.Contains("ETag"));
            Assert.False(response.Headers.Contains("Accept-Ranges"));
            Assert.Null(response.Content.Headers.ContentRange);
            Assert.Null(response.Content.Headers.LastModified);
            AssertPrivateDownloadHeaders(response);
            AssertSecurityHeaders(response);
            await AssertExportLinkAsync(host);
        }

        Assert.Equal(3, service.DownloadCalls);
        Assert.Equal(AdminPageFixtureData.BatchId, service.LastBatchId);
        Assert.True(service.LastActorId != Guid.Empty, "The authenticated administrator identity must reach the export service.");
        Assert.Equal(0, service.MutationCalls);
        Assert.Equal(0, host.Data.MutationCalls);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("invalid")]
    [InlineData("revoked")]
    public async Task Direct_export_requests_with_no_valid_administrator_never_call_the_service(string credentialState)
    {
        var service = new BatchServiceSpy(available: true);
        await using var host = await AdminPageTestHost.StartAsync(batchService: service);
        if (credentialState == "invalid")
        {
            // Random, invalid ticket material is generated and used only in memory.
            host.Client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",
                "LicensePlatform.Admin=" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        }
        else if (credentialState == "revoked")
        {
            await host.AuthenticateAsync();
            await host.RevokeAdministratorCookieAsync();
        }

        using var response = await host.Client.GetAsync(ExportPath);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Account/Login", response.Headers.Location.AbsolutePath);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        Assert.True(query.TryGetValue("ReturnUrl", out var returnUrl)
            && string.Equals(returnUrl.ToString(), ExportPath, StringComparison.Ordinal),
            "Authentication must retain the local export route without invoking the export service.");
        Assert.Equal(0, service.DownloadCalls);
        Assert.Equal(0, service.ListCalls);
        Assert.Equal(0, service.MutationCalls);
        Assert.False(response.Content.Headers.ContentType?.MediaType == "text/csv");
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Null_export_redirects_with_safe_tempdata_and_no_csv_in_the_rendered_page()
    {
        var service = new BatchServiceSpy(available: false);
        await using var host = await AdminPageTestHost.StartAsync(batchService: service);
        await host.AuthenticateAsync();

        using var response = await host.Client.GetAsync(ExportPath);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.Location?.OriginalString == "/Admin/Batches", "An unavailable export must redirect only to the batch list.");
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.False(response.Content.Headers.ContentType?.MediaType == "text/csv");
        AssertPrivateDownloadHeaders(response);
        AssertSecurityHeaders(response);
        var redirectBody = await response.Content.ReadAsByteArrayAsync();
        Assert.False(Encoding.UTF8.GetString(redirectBody).Contains(Encoding.UTF8.GetString(service.Content), StringComparison.Ordinal),
            "An unavailable response must not embed export bytes.");

        // Forward only the test response's TempData cookie, keeping it out of assertion output.
        var tempCookies = response.Headers.TryGetValues("Set-Cookie", out var cookieHeaders)
            ? cookieHeaders.Where(value => value.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider", StringComparison.Ordinal))
                .Select(value => value.Split(';', 2)[0]).ToArray()
            : [];
        Assert.True(tempCookies.Length > 0, "The safe unavailable message must survive the local redirect.");
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/Admin/Batches");
        var administratorCookie = host.Client.DefaultRequestHeaders.GetValues("Cookie");
        pageRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", administratorCookie.Concat(tempCookies)));
        using var page = await host.Client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        var text = WebUtility.HtmlDecode(html);
        Assert.True(text.Contains(UnavailableMessage, StringComparison.Ordinal), "The batch page must explain cleared legacy data and remaining archive decryption configuration.");
        Assert.True(text.Contains("无可用导出数据", StringComparison.Ordinal), "Unavailable batch rows must expose the unavailable-data label.");
        Assert.False(text.Contains("已下载或已超过 15 分钟", StringComparison.Ordinal), "The obsolete one-time expiration error must not be shown.");
        Assert.False(text.Contains(Encoding.UTF8.GetString(service.Content), StringComparison.Ordinal), "Export bytes must never be embedded in the HTML page.");
        Assert.Equal(1, service.DownloadCalls);
        Assert.Equal(0, service.MutationCalls);
        AssertSecurityHeaders(page);
    }

    [Fact]
    public async Task Null_handler_stores_only_the_fixed_safe_message_in_tempdata()
    {
        var service = new BatchServiceSpy(available: false);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D"))], "fixture"))
        };
        var cacheHeadersPrecedeService = false;
        service.OnDownload = () => cacheHeadersPrecedeService = context.Response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase)
            && context.Response.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase)
            && context.Response.Headers.Pragma.ToString() == "no-cache";
        var page = new IndexModel(service)
        {
            PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new EmptyTempDataProvider())
        };

        var result = await page.OnGetExportAsync(AdminPageFixtureData.BatchId, CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(cacheHeadersPrecedeService, "Private download cache headers must be set before the export service is invoked.");
        Assert.True(page.TempData.Count == 1
            && page.TempData.Peek("Error") is string message
            && string.Equals(message, UnavailableMessage, StringComparison.Ordinal),
            "TempData must contain only the fixed unavailable message, never CSV or decryption details.");
        Assert.True(context.Response.Headers.CacheControl.ToString().Contains("no-store", StringComparison.OrdinalIgnoreCase)
            && context.Response.Headers.CacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase),
            "The handler must set private download cache policy even when no file is available.");
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Default_rendering_fixture_stays_download_null_and_management_posts_still_require_antiforgery()
    {
        await using (var host = await AdminPageTestHost.StartAsync())
        {
            await host.AuthenticateAsync();
            using var response = await host.Client.GetAsync(ExportPath);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal(0, host.Data.MutationCalls);
        }

        var service = new BatchServiceSpy(available: true);
        await using var overriddenHost = await AdminPageTestHost.StartAsync(batchService: service);
        await overriddenHost.AuthenticateAsync();
        using var rejected = await overriddenHost.Client.PostAsync("/Admin/Batches?handler=DisableAll",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id"] = AdminPageFixtureData.BatchId.ToString("D"),
                ["reason"] = "fixture-safe-reason"
            }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, service.DownloadCalls);
        Assert.Equal(0, service.MutationCalls);
        Assert.Equal(0, overriddenHost.Data.MutationCalls);
        AssertSecurityHeaders(rejected);
    }

    private static async Task AssertExportLinkAsync(AdminPageTestHost host)
    {
        using var response = await host.Client.GetAsync("/Admin/Batches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var links = Regex.Matches(html, "<a\\b[^>]*href=[\"']([^\"']*)[\"'][^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        var exportLinks = links.Cast<Match>().Where(match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            var uri = new Uri(host.BaseUri, href);
            var query = QueryHelpers.ParseQuery(uri.Query);
            return uri.AbsolutePath == "/Admin/Batches"
                && query.TryGetValue("handler", out var handler) && handler.ToString() == "Export"
                && query.TryGetValue("id", out var id) && id.ToString() == AdminPageFixtureData.BatchId.ToString("D");
        }).ToArray();
        Assert.True(exportLinks.Length == 1, "The original GET export route must remain available after every download.");
        var label = WebUtility.HtmlDecode(Regex.Replace(exportLinks[0].Groups[2].Value, "<[^>]*>", "", RegexOptions.Singleline, RegexTimeout)).Trim();
        Assert.Equal("导出卡密", label);
        var text = WebUtility.HtmlDecode(html);
        Assert.True(text.Contains("无可用导出数据", StringComparison.Ordinal), "Rows without an archive must explain unavailable export data.");
        Assert.False(text.Contains("一次性下载 CSV", StringComparison.Ordinal), "Repeatable exports must not retain the obsolete one-time action label.");
        Assert.False(text.Contains("生成 15 分钟后自动删除", StringComparison.Ordinal), "Archive guidance must not promise automatic fifteen-minute deletion.");
    }

    private static void AssertPrivateDownloadHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true && response.Headers.CacheControl.NoCache,
            "Export responses, including unavailable redirects, must explicitly disable storage and caching.");
        Assert.True(response.Headers.Pragma.Any(value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase)),
            "Export responses must also carry Pragma: no-cache.");
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", string.Join("", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", string.Join("", response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join("", response.Headers.GetValues("Referrer-Policy")));
        var permissions = string.Join("", response.Headers.GetValues("Permissions-Policy"));
        Assert.True(permissions.Contains("camera=()", StringComparison.Ordinal) && permissions.Contains("microphone=()", StringComparison.Ordinal));
        var csp = string.Join("", response.Headers.GetValues("Content-Security-Policy"));
        Assert.True(csp.Contains("default-src 'self'", StringComparison.Ordinal)
            && csp.Contains("frame-ancestors 'none'", StringComparison.Ordinal)
            && csp.Contains("form-action 'self'", StringComparison.Ordinal)
            && csp.Contains("object-src 'none'", StringComparison.Ordinal), "Downloads must retain the real security middleware headers.");
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class BatchServiceSpy(bool available) : ILicenseBatchAdminService
    {
        private readonly ILicenseBatchAdminService _presentation = new AdminPageFixtureData(AdminPageDataState.Populated);
        private int _downloadCalls;
        private int _listCalls;
        private int _mutationCalls;
        public byte[] Content { get; } = Encoding.UTF8.GetBytes("fixture_export_marker," + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\r\n");
        public string FileName => $"license-batch-{AdminPageFixtureData.BatchId:N}.csv";
        public int DownloadCalls => Volatile.Read(ref _downloadCalls);
        public int ListCalls => Volatile.Read(ref _listCalls);
        public int MutationCalls => Volatile.Read(ref _mutationCalls);
        public Guid LastBatchId { get; private set; }
        public Guid LastActorId { get; private set; }
        public Action? OnDownload { get; set; }

        public async Task<IReadOnlyList<BatchListItem>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listCalls);
            var rows = await _presentation.ListAsync(cancellationToken);
            return rows.Select(row => row.Id == AdminPageFixtureData.BatchId ? row with { ExportAvailable = available } : row).ToArray();
        }

        public Task<IReadOnlyList<ProductOption>> GetProductOptionsAsync(CancellationToken cancellationToken) =>
            _presentation.GetProductOptionsAsync(cancellationToken);

        public Task<BatchExportDownload?> DownloadExportAsync(Guid batchId, Guid actorId, string sourceIp, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloadCalls);
            LastBatchId = batchId;
            LastActorId = actorId;
            OnDownload?.Invoke();
            return Task.FromResult<BatchExportDownload?>(available
                ? new BatchExportDownload(FileName, Content.ToArray(), "text/csv; charset=utf-8")
                : null);
        }

        public Task<Guid> CreateAsync(CreateBatchCommand command, Guid actorId, string sourceIp, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mutationCalls);
            throw new InvalidOperationException("Export page spies cannot create batches.");
        }

        public Task DisableAllAsync(Guid batchId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _mutationCalls);
            throw new InvalidOperationException("Export page spies cannot change licenses.");
        }
    }
}
