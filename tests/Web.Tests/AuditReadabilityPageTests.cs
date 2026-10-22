using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AuditReadabilityPageTests
{
    private const string ActorId = "10000000-0000-0000-0000-000000000022";
    private const string TargetId = "20000000-0000-0000-0000-000000000022";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 22, 16, 45, 12, TimeSpan.FromHours(8));

    [Fact]
    public async Task Chinese_action_select_submits_raw_codes_and_preserves_filter_in_paging_without_resubmitting_the_old_page()
    {
        var data = new AuditPageData([BasicRow()], totalCount: 151);
        await using var host = await AdminPageTestHost.StartAsync(auditService: data);
        await host.AuthenticateAsync();
        var html = await GetHtmlAsync(host, "/Admin/Audit?action=BATCH.CREATE&pageNumber=2");
        var filter = GetFilterForm(html);
        var select = GetActionSelect(filter.Body);
        var options = Elements(select.Body, "option");

        Assert.True(Attribute(select.OpeningTag, "name") == "action" && Attribute(select.OpeningTag, "id").Length > 0,
            "The Chinese action selector must keep the existing action GET field.");
        Assert.True(OpeningTags(filter.Body, "label").Count(tag => Attribute(tag, "for") == Attribute(select.OpeningTag, "id")) == 1,
            "The action selector needs exactly one associated label.");
        foreach (var choice in AuditPresentationFormatter.Actions)
        {
            var matching = options.Where(option => Attribute(option.OpeningTag, "value") == choice.Code).ToArray();
            Assert.True(matching.Length == 1 && PlainText(matching[0].Body).Contains(choice.Label, StringComparison.Ordinal),
                "Each Chinese action option must submit its original raw event code.");
        }
        Assert.True(options.Any(option => Attribute(option.OpeningTag, "value") == "BATCH.CREATE" && HasAttribute(option.OpeningTag, "selected")),
            "The selected action must survive the GET response.");
        Assert.True(OpeningTags(filter.Body, "input").Where(tag => Attribute(tag, "name") == "pageNumber")
            .All(tag => Attribute(tag, "value") == "1"), "Submitting a new filter must reset to the first page, not retain the old page number.");
        Assert.True(data.LastPage == 2 && data.LastPageSize == 50 && data.LastAction == "BATCH.CREATE",
            "The existing pageNumber and fixed page-size contract must reach the read-only audit service.");
        AssertPagingLink(host, html, "上一页", 1, "BATCH.CREATE");
        AssertPagingLink(host, html, "下一页", 3, "BATCH.CREATE");
        AssertResetLink(host, html);
        Assert.Equal(0, host.Data.MutationCalls);
    }

    [Theory]
    [InlineData("LiCeNsE.")]
    [InlineData("legacy.<filter-probe>&")]
    public async Task Partial_and_custom_action_values_stay_selected_encoded_and_preserved_by_paging(string action)
    {
        var data = new AuditPageData([BasicRow()], totalCount: 151);
        await using var host = await AdminPageTestHost.StartAsync(auditService: data);
        await host.AuthenticateAsync();
        var html = await GetHtmlAsync(host, "/Admin/Audit?pageNumber=2&action=" + Uri.EscapeDataString("  " + action + "  "));
        var options = Elements(GetActionSelect(GetFilterForm(html).Body).Body, "option");
        Assert.True(options.Any(option => Attribute(option.OpeningTag, "value") == action && HasAttribute(option.OpeningTag, "selected")),
            "An old URL's custom or partial action must not be silently replaced by a known code.");
        Assert.True(data.LastAction == action, "The page must retain the existing trimmed, otherwise unchanged action query.");
        Assert.True(OpeningTags(html, "filter-probe").Count == 0, "A custom filter value must be Razor encoded rather than interpreted as markup.");
        AssertPagingLink(host, html, "上一页", 1, action);
        AssertPagingLink(host, html, "下一页", 3, action);
        AssertResetLink(host, html);
    }

    [Fact]
    public async Task Empty_out_of_range_pages_offer_first_page_with_the_filter_and_a_separate_clear_filter_link()
    {
        const string action = "LiCeNsE.";
        var data = new AuditPageData([], totalCount: 101);
        await using var host = await AdminPageTestHost.StartAsync(auditService: data);
        await host.AuthenticateAsync();
        var html = await GetHtmlAsync(host, "/Admin/Audit?pageNumber=9&action=" + Uri.EscapeDataString(action));
        Assert.True(data.LastPage == 9 && data.LastAction == action, "An empty out-of-range page must retain the requested paging/filter contract.");
        Assert.True(OpeningTags(html, "table").Count == 0, "An empty page must not render placeholder audit rows.");
        Assert.True(Elements(html, "a").Any(link => PlainText(link.Body).Contains("第一页", StringComparison.Ordinal)
            && IsAuditLink(host, link, 1, action)), "The recovery link must return to page one without dropping the action filter.");
        AssertResetLink(host, html);
    }

    [Fact]
    public async Task Actual_Razor_uses_shared_presentation_encoded_public_names_native_details_and_UTC_without_raw_summary_or_secrets()
    {
        const string publicPrefix = "Public-<audit-probe data-probe=\"encoded\">&";
        var publicName = publicPrefix + new string('W', 500);
        var sensitiveMarker = "synthetic-sensitive-" + Guid.NewGuid().ToString("N");
        var summaryMarker = "raw-summary-" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            Name = publicName,
            Code = "public_code_snapshot",
            note = sensitiveMarker,
            reason = sensitiveMarker,
            deviceId = sensitiveMarker,
            fingerprint = sensitiveMarker,
            token = sensitiveMarker,
            unknown = sensitiveMarker
        });
        var presentation = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", payload,
            AuditActorType.Administrator, ActorId, TargetId);
        var row = new AuditListItem(Timestamp, ActorId, "PRODUCT.UPDATE", "Product", TargetId, "RECORDED", "192.0.2.10", summaryMarker)
        {
            Presentation = presentation
        };
        var nonstandard = new AuditListItem(Timestamp.AddMinutes(-1), sensitiveMarker, "FIXTURE.<event-probe>&", "Product",
            sensitiveMarker, "SUCCESS", sensitiveMarker, summaryMarker);
        var data = new AuditPageData([row, nonstandard],
            [new RecentAuditItem(Timestamp, row.Action, row.TargetType, "RECORDED", summaryMarker) { Presentation = presentation }], totalCount: 2);
        await using var host = await AdminPageTestHost.StartAsync(auditService: data, dashboardQuery: data);
        await host.AuthenticateAsync();
        foreach (var path in new[] { "/Admin/Audit", "/Admin/Dashboard" })
        {
            var html = await GetHtmlAsync(host, path);
            var text = PlainText(html);
            Assert.True(text.Contains(presentation.ActionLabel, StringComparison.Ordinal)
                && text.Contains(presentation.Description, StringComparison.Ordinal)
                && text.Contains(presentation.TargetLabel, StringComparison.Ordinal), "Both actual Razor pages must show the shared formatter's readable presentation.");
            Assert.True(text.Contains(publicPrefix, StringComparison.Ordinal), "An allowed public snapshot must remain readable after encoding and bounding.");
            Assert.True(OpeningTags(html, "audit-probe").Count == 0 && OpeningTags(html, "event-probe").Count == 0,
                "Public names and raw event-code details must not become active markup.");
            Assert.False(WebUtility.HtmlDecode(html).Contains(publicName, StringComparison.Ordinal), "A long snapshot must not bypass the display-value limit.");
            Assert.False(WebUtility.HtmlDecode(html).Contains(sensitiveMarker, StringComparison.Ordinal),
                "Secrets, free text, raw device IDs, and arbitrary identifiers/source strings must be absent even from closed details.");
            Assert.False(WebUtility.HtmlDecode(html).Contains(summaryMarker, StringComparison.Ordinal), "Raw Summary must never be emitted anywhere in the HTML.");
            Assert.True(text.Contains("2026-10-22 08:45:12", StringComparison.Ordinal)
                && text.Contains("UTC", StringComparison.Ordinal), "The actual timestamp and its time-zone label must both be UTC.");
            Assert.False(Elements(html, "th").Any(header => PlainText(header.Body) == "结果"), "There must be no generic success-implying outcome column.");
            var details = Elements(html, "details");
            Assert.True(details.Count > 0, "Audit technical information must use a native, keyboard-operable details disclosure.");
            foreach (var detail in details)
            {
                var summaries = Elements(detail.Body, "summary");
                Assert.True(summaries.Count == 1 && PlainText(summaries[0].Body).Contains("查看详情", StringComparison.Ordinal)
                    && Attribute(summaries[0].OpeningTag, "tabindex") != "-1", "Each native details disclosure needs a readable summary without suppressing keyboard focus.");
            }
            Assert.True(OpeningTags(html, "div").Any(tag => Attribute(tag, "role") == "region"
                && Attribute(tag, "tabindex") == "0" && Attribute(tag, "aria-label").Length > 0),
                "The locally scrolling audit table needs a named, keyboard-focusable region.");
            if (path == "/Admin/Audit")
            {
                var technicalText = string.Join("\n", details.Select(detail => PlainText(detail.Body)));
                Assert.True(technicalText.Contains(ActorId, StringComparison.Ordinal)
                    && technicalText.Contains(TargetId, StringComparison.Ordinal)
                    && technicalText.Contains("192.0.2.10", StringComparison.Ordinal)
                    && technicalText.Contains("PRODUCT.UPDATE", StringComparison.Ordinal), "Recognized existing identifiers, IP, and event code belong in expandable technical details.");
            }
        }
        Assert.Equal(0, host.Data.MutationCalls);
    }

    [Theory]
    [InlineData("/Admin/Audit")]
    [InlineData("/Admin/Dashboard")]
    public async Task Default_legacy_fixture_is_read_only_and_never_renders_raw_summary_or_false_outcome_badges(string path)
    {
        await using var host = await AdminPageTestHost.StartAsync(AdminPageDataState.LongText);
        await host.AuthenticateAsync();
        var html = await GetHtmlAsync(host, path);
        var text = PlainText(html);
        Assert.True(text.Contains("其他操作", StringComparison.Ordinal), "Legacy fixture actions need the safe unknown-action fallback.");
        Assert.False(WebUtility.HtmlDecode(html).Contains(host.Data.Summary, StringComparison.Ordinal), "Legacy Summary must not leak through a fallback or closed details section.");
        Assert.False(text.Contains("SUCCESS", StringComparison.Ordinal) || text.Contains("FAILURE", StringComparison.Ordinal),
            "Uninterpreted fixture Result codes must not be promoted into visible outcomes.");
        Assert.False(Elements(html, "span").Where(span => Attribute(span.OpeningTag, "class").Contains("badge-state", StringComparison.Ordinal))
            .Any(span => PlainText(span.Body) is "成功" or "失败"), "Unknown events must not get a false success/failure badge.");
        Assert.Equal(0, host.Data.MutationCalls);
        Assert.Equal(0, host.Data.AuthenticationCalls);
    }

    [Theory]
    [InlineData("/Admin/Audit")]
    [InlineData("/Admin/Dashboard")]
    public async Task Anonymous_requests_never_reach_optional_audit_or_dashboard_services(string path)
    {
        var data = new AuditPageData([BasicRow()]);
        await using var host = await AdminPageTestHost.StartAsync(auditService: data, dashboardQuery: data);
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.Location?.AbsolutePath == "/Account/Login", "The existing authentication boundary must precede either read-only service.");
        Assert.Equal(0, data.SearchCalls);
        Assert.Equal(0, data.DashboardCalls);
        Assert.Equal(0, host.Data.MutationCalls);
    }

    private static AuditListItem BasicRow() => new(Timestamp, "System", "BATCH.CREATE", "LicenseBatch", TargetId,
        "RECORDED", "local-cli", "fixture-raw-summary-not-for-display");

    private static async Task<string> GetHtmlAsync(AdminPageTestHost host, string path)
    {
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentType?.MediaType == "text/html", "The real compiled Razor page must render HTML.");
        return await response.Content.ReadAsStringAsync();
    }

    private static HtmlElement GetFilterForm(string html)
    {
        var forms = Elements(html, "form").Where(form => string.Equals(Attribute(form.OpeningTag, "method"), "get", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(forms.Length == 1, "Audit filtering must remain one GET form.");
        return forms[0];
    }

    private static HtmlElement GetActionSelect(string html)
    {
        var selects = Elements(html, "select").Where(select => Attribute(select.OpeningTag, "name") == "action").ToArray();
        Assert.True(selects.Length == 1, "The action filter must be a Chinese select rather than a raw event-code input.");
        return selects[0];
    }

    private static void AssertPagingLink(AdminPageTestHost host, string html, string label, int page, string action) =>
        Assert.True(Elements(html, "a").Any(link => PlainText(link.Body) == label && IsAuditLink(host, link, page, action)),
            "Audit paging links must preserve both the action filter and pageNumber query parameter.");

    private static void AssertResetLink(AdminPageTestHost host, string html) =>
        Assert.True(Elements(html, "a").Any(link =>
        {
            var label = PlainText(link.Body);
            var uri = new Uri(host.BaseUri, Attribute(link.OpeningTag, "href"));
            var query = QueryHelpers.ParseQuery(uri.Query);
            return IsAuditPath(uri.AbsolutePath) && !query.ContainsKey("action")
                && (!query.TryGetValue("pageNumber", out var page) || page.ToString() == "1")
                && (label.Contains("清除", StringComparison.Ordinal) || label.Contains("查看全部", StringComparison.Ordinal)
                    || label.Contains("全部记录", StringComparison.Ordinal));
        }), "The filter needs a distinct clear/view-all link that also resets paging.");

    private static bool IsAuditLink(AdminPageTestHost host, HtmlElement link, int page, string action)
    {
        var uri = new Uri(host.BaseUri, Attribute(link.OpeningTag, "href"));
        var query = QueryHelpers.ParseQuery(uri.Query);
        return IsAuditPath(uri.AbsolutePath)
            && query.TryGetValue("pageNumber", out var actualPage) && actualPage.ToString() == page.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && query.TryGetValue("action", out var actualAction) && actualAction.ToString() == action;
    }

    private static bool IsAuditPath(string path) => path.TrimEnd('/') is "/Admin/Audit" or "/Admin/Audit/Index";

    // Boolean assertions and narrowly extracted fields avoid printing token-bearing full HTML on failure.
    private sealed record HtmlElement(string OpeningTag, string Body);
    private static IReadOnlyList<HtmlElement> Elements(string html, string name) => Regex.Matches(html,
        "(<" + Regex.Escape(name) + "\\b[^>]*>)(.*?)</" + Regex.Escape(name) + ">",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout)
        .Select(match => new HtmlElement(match.Groups[1].Value, match.Groups[2].Value)).ToArray();
    private static IReadOnlyList<string> OpeningTags(string html, string name) => Regex.Matches(html,
        "<" + Regex.Escape(name) + "\\b[^>]*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout)
        .Select(match => match.Value).ToArray();
    private static bool HasAttribute(string tag, string name) => Regex.IsMatch(tag,
        "\\s" + Regex.Escape(name) + "(?:\\s|=|/?>)", RegexOptions.IgnoreCase, RegexTimeout);
    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            RegexOptions.IgnoreCase, RegexTimeout);
        return match.Success ? WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value) : string.Empty;
    }
    private static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "", RegexOptions.Singleline, RegexTimeout)).Trim();

    private sealed class AuditPageData(IReadOnlyList<AuditListItem> rows, IReadOnlyList<RecentAuditItem>? recent = null, long totalCount = 151)
        : IAuditAdminService, IAdminDashboardQuery
    {
        public int SearchCalls { get; private set; }
        public int DashboardCalls { get; private set; }
        public int LastPage { get; private set; }
        public int LastPageSize { get; private set; }
        public string? LastAction { get; private set; }
        public Task<AuditSearchResult> SearchAsync(int page, int pageSize, string? action, CancellationToken cancellationToken)
        {
            SearchCalls++;
            LastPage = page;
            LastPageSize = pageSize;
            LastAction = action;
            return Task.FromResult(new AuditSearchResult(rows, page, pageSize, totalCount));
        }
        public Task<AdminDashboardSnapshot> GetAsync(CancellationToken cancellationToken)
        {
            DashboardCalls++;
            return Task.FromResult(new AdminDashboardSnapshot(0, 0, 0, 0, 0, recent ?? []));
        }
    }
}
