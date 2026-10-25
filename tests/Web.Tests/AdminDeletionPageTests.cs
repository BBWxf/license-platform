using System.Net;
using System.Text.RegularExpressions;
using LicensePlatform.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminDeletionPageTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Confirmed_post_uses_authenticated_actor_and_redirects_to_list(bool product)
    {
        var spy = new DeletionSpy();
        await using var host = await AdminPageTestHost.StartAsync(productService: spy, licenseService: spy);
        await host.AuthenticateAsync();
        using var response = await PostAsync(host, product, "true", "fixture deletion");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(product ? "/Admin/Products" : "/Admin/Licenses", response.Headers.Location?.OriginalString);
        Assert.Equal(1, spy.Calls);
        Assert.Equal(product ? AdminPageFixtureData.ProductId : AdminPageFixtureData.LicenseId, spy.Id);
        Assert.Equal(Guid.Parse("10000000-0000-0000-0000-000000000001"), spy.ActorId);
        if (!product)
        {
            Assert.Equal("fixture deletion", spy.Reason);
        }
    }

    [Theory]
    [InlineData(true, "false", "reason")]
    [InlineData(false, "false", "reason")]
    [InlineData(true, "not-a-bool", "reason")]
    [InlineData(false, "not-a-bool", "reason")]
    [InlineData(false, "true", " ")]
    [InlineData(false, "true", "too-long")]
    public async Task Missing_confirmation_or_invalid_reason_never_calls_delete(bool product, string confirmed, string reason)
    {
        var spy = new DeletionSpy();
        await using var host = await AdminPageTestHost.StartAsync(productService: spy, licenseService: spy);
        await host.AuthenticateAsync();
        using var response = await PostAsync(host, product, confirmed, reason == "too-long" ? new string('x', 301) : reason);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, spy.Calls);
    }

    [Theory]
    [InlineData(true, "anonymous")]
    [InlineData(false, "anonymous")]
    [InlineData(true, "revoked")]
    [InlineData(false, "revoked")]
    [InlineData(true, "no-token")]
    [InlineData(false, "no-token")]
    public async Task Unauthorized_or_forged_posts_never_call_delete(bool product, string state)
    {
        var spy = new DeletionSpy();
        await using var host = await AdminPageTestHost.StartAsync(productService: spy, licenseService: spy);
        if (state != "anonymous")
        {
            await host.AuthenticateAsync();
        }
        if (state == "revoked")
        {
            await host.RevokeAdministratorCookieAsync();
        }
        using var response = await host.Client.PostAsync(Path(product) + "?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = AdminPageFixtureData.ProductId.ToString("D"),
            ["confirmed"] = "true",
            ["Action.Reason"] = "fixture deletion"
        }));
        Assert.Equal(state == "no-token" ? HttpStatusCode.BadRequest : HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, spy.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Missing_target_or_database_conflict_has_safe_feedback(bool product, bool conflict)
    {
        var spy = new DeletionSpy { Exists = false, Conflict = conflict };
        await using var host = await AdminPageTestHost.StartAsync(productService: spy, licenseService: spy);
        await host.AuthenticateAsync();
        using var response = await PostAsync(host, product, "true", "fixture deletion");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(1, spy.Calls);
        var tempCookies = response.Headers.GetValues("Set-Cookie")
            .Where(value => value.StartsWith(".AspNetCore.Mvc.CookieTempDataProvider", StringComparison.Ordinal))
            .Select(value => value.Split(';', 2)[0]);
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, response.Headers.Location);
        pageRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", host.Client.DefaultRequestHeaders.GetValues("Cookie").Concat(tempCookies)));
        using var page = await host.Client.SendAsync(pageRequest);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var text = WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync());
        Assert.Contains(conflict ? "删除未完成" : "不存在或已被删除", text);
        Assert.DoesNotContain("fixture-provider-detail", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Get_with_delete_handler_never_mutates(bool product)
    {
        var spy = new DeletionSpy();
        await using var host = await AdminPageTestHost.StartAsync(productService: spy, licenseService: spy);
        await host.AuthenticateAsync();
        using var response = await host.Client.GetAsync(Path(product) + "?handler=Delete&confirmed=true");
        Assert.Equal(0, spy.Calls);
    }

    private static string Path(bool product) => product ? "/Admin/Products" : "/Admin/Licenses/Details/" + AdminPageFixtureData.LicenseId;

    private static async Task<HttpResponseMessage> PostAsync(AdminPageTestHost host, bool product, string confirmed, string reason)
    {
        using var page = await host.Client.GetAsync(Path(product));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.None, RegexTimeout);
        Assert.True(token.Success, "The real Razor page must supply an antiforgery token.");
        var cookies = page.Headers.GetValues("Set-Cookie").Where(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            .Select(value => value.Split(';', 2)[0]);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path(product) + "?handler=Delete");
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", host.Client.DefaultRequestHeaders.GetValues("Cookie").Concat(cookies)));
        var fields = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value),
            ["confirmed"] = confirmed,
            ["Action.Reason"] = reason,
            ["actorId"] = Guid.NewGuid().ToString("D")
        };
        if (product)
        {
            fields["id"] = AdminPageFixtureData.ProductId.ToString("D");
        }
        request.Content = new FormUrlEncodedContent(fields);
        return await host.Client.SendAsync(request);
    }

    private sealed class DeletionSpy : IProductAdminService, ILicenseAdminService
    {
        private readonly AdminPageFixtureData _data = new(AdminPageDataState.Populated);
        public int Calls { get; private set; }
        public Guid Id { get; private set; }
        public Guid ActorId { get; private set; }
        public string? Reason { get; private set; }
        public bool Exists { get; init; } = true;
        public bool Conflict { get; init; }

        public Task<bool> DeleteAsync(Guid id, Guid actorId, string sourceIp, CancellationToken cancellationToken) => DeleteAsync(id, "", actorId, sourceIp, cancellationToken);
        public Task<bool> DeleteAsync(Guid id, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken)
        {
            Calls++;
            Id = id;
            ActorId = actorId;
            Reason = reason;
            if (Conflict)
            {
                throw new DbUpdateConcurrencyException("fixture-provider-detail");
            }
            return Task.FromResult(Exists);
        }

        public Task<IReadOnlyList<ProductListItem>> ListAsync(CancellationToken cancellationToken) => ((IProductAdminService)_data).ListAsync(cancellationToken);
        Task<ProductEditor?> IProductAdminService.GetAsync(Guid id, CancellationToken cancellationToken) => ((IProductAdminService)_data).GetAsync(id, cancellationToken);
        public Task<Guid> SaveAsync(Guid? id, ProductEditor input, Guid actorId, string sourceIp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEnabledAsync(Guid id, bool enabled, Guid actorId, string sourceIp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientConfigDownload?> GetClientConfigAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LicenseSearchResult> SearchAsync(LicenseSearchQuery query, CancellationToken cancellationToken) => ((ILicenseAdminService)_data).SearchAsync(query, cancellationToken);
        Task<LicenseDetails?> ILicenseAdminService.GetAsync(Guid id, CancellationToken cancellationToken) => ((ILicenseAdminService)_data).GetAsync(id, cancellationToken);
        public Task SetEnabledAsync(Guid id, bool enabled, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ExtendAsync(Guid id, AdminValidityKind kind, int units, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UnbindAsync(Guid bindingId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
