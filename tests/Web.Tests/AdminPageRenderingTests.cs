using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminPageRenderingTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly string ProductEdit = "/Admin/Products/Edit/" + AdminPageFixtureData.ProductId;
    private static readonly string LicenseDetails = "/Admin/Licenses/Details/" + AdminPageFixtureData.LicenseId;
    private static readonly (string Path, string Navigation)[] ContentPages =
    [
        ("/Admin/Dashboard", "/Admin/Dashboard"),
        ("/Admin/Products", "/Admin/Products"),
        ("/Admin/Products/Edit", "/Admin/Products"),
        (ProductEdit, "/Admin/Products"),
        ("/Admin/Batches", "/Admin/Batches"),
        ("/Admin/Batches/Create", "/Admin/Batches"),
        ("/Admin/Licenses", "/Admin/Licenses"),
        (LicenseDetails, "/Admin/Licenses"),
        ("/Admin/Audit", "/Admin/Audit"),
        ("/Admin/Settings", "/Admin/Settings")
    ];

    [Fact]
    public async Task Anonymous_content_requests_redirect_to_login_with_a_local_return_url()
    {
        await using var host = await AdminPageTestHost.StartAsync();
        foreach (var (path, _) in ContentPages)
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
            Assert.Equal("/Account/Login", response.Headers.Location.AbsolutePath);
            var returnUrl = WebUtility.UrlDecode(response.Headers.Location.Query);
            Assert.True(returnUrl.Contains("ReturnUrl=" + path, StringComparison.Ordinal), "Login redirect must retain the requested local page.");
            AssertSecurityHeaders(response);
        }
    }

    [Theory]
    [InlineData(AdminPageDataState.Empty)]
    [InlineData(AdminPageDataState.Populated)]
    [InlineData(AdminPageDataState.LongText)]
    public async Task Actual_razor_content_pages_render_fixture_states_and_mark_the_parent_navigation(AdminPageDataState state)
    {
        await using var host = await AdminPageTestHost.StartAsync(state);
        await host.AuthenticateAsync();
        foreach (var (path, navigation) in ContentPages)
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(Tags(html, "main").Count == 1, "A rendered page needs one main landmark.");
            Assert.True(Tags(html, "h1").Count == 1, "A rendered page needs one page heading.");
            var activeLinks = Tags(html, "a").Where(tag => Attribute(tag, "aria-current") == "page").ToArray();
            Assert.True(activeLinks.Length == 1, "Exactly one primary navigation link must be current, including on child pages.");
            Assert.Equal(navigation, NormalizePath(Attribute(activeLinks[0], "href")));
            AssertSecurityHeaders(response);
            AssertPostFormsHaveAntiforgery(html);

            if (state == AdminPageDataState.Empty && IsDataTablePage(path))
            {
                Assert.True(Tags(html, "table").Count == 0, "An empty fixture must render the empty state rather than placeholder table rows.");
            }
            else if (state != AdminPageDataState.Empty)
            {
                AssertSuppliedContent(host, path, html);
            }

            if (state == AdminPageDataState.LongText && HasLongFixtureContent(path))
            {
                Assert.True(WebUtility.HtmlDecode(html).Contains(AdminPageFixtureData.EncodingProbe, StringComparison.Ordinal),
                    "Long fixture text must remain present rather than being silently replaced.");
                Assert.True(Tags(html, "render-probe").Count == 0, "Fixture text must be HTML encoded, not interpreted as markup.");
            }
        }
    }

    [Theory]
    [InlineData(AdminPageDataState.Empty)]
    [InlineData(AdminPageDataState.Populated)]
    [InlineData(AdminPageDataState.LongText)]
    public async Task Rendered_controls_have_unique_ids_and_exactly_one_associated_label(AdminPageDataState state)
    {
        await using var host = await AdminPageTestHost.StartAsync(state);
        using (var login = await host.Client.GetAsync("/Account/Login"))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            AssertAccessibleFormLabelsAndUniqueIds(await login.Content.ReadAsStringAsync(), "/Account/Login");
        }

        await host.AuthenticateAsync();
        foreach (var (path, _) in ContentPages)
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertAccessibleFormLabelsAndUniqueIds(await response.Content.ReadAsStringAsync(), path);
        }
    }

    [Theory]
    [InlineData(AdminPageDataState.Empty)]
    [InlineData(AdminPageDataState.Populated)]
    [InlineData(AdminPageDataState.LongText)]
    public async Task Dashboard_stat_tiles_show_real_supplied_counts_not_design_placeholders(AdminPageDataState state)
    {
        await using var host = await AdminPageTestHost.StartAsync(state);
        await host.AuthenticateAsync();
        using var response = await host.Client.GetAsync("/Admin/Dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var expected = new Dictionary<string, long>
        {
            ["products"] = state == AdminPageDataState.Empty ? 0 : 7,
            ["unused"] = state == AdminPageDataState.Empty ? 0 : 1234,
            ["active"] = state == AdminPageDataState.Empty ? 0 : 567,
            ["expired"] = state == AdminPageDataState.Empty ? 0 : 89,
            ["bindings"] = state == AdminPageDataState.Empty ? 0 : 321
        };
        foreach (var (stat, value) in expected)
        {
            var starts = Matches(html, "<[^>]+\\bdata-stat=[\"']" + stat + "[\"'][^>]*>");
            Assert.True(starts.Count == 1, "Each dashboard count must have one stable data-stat tile.");
            var section = html[starts[0].Index..];
            var nextStat = Regex.Match(section[starts[0].Length..], "\\bdata-stat=", RegexOptions.None, RegexTimeout);
            if (nextStat.Success)
            {
                section = section[..(starts[0].Length + nextStat.Index)];
            }

            var number = Regex.Match(section, "<[^>]+class=[\"'][^\"']*\\bmetric-value\\b[^\"']*[\"'][^>]*>(.*?)</[^>]+>",
                RegexOptions.Singleline, RegexTimeout);
            Assert.True(number.Success, "Dashboard tiles must expose a readable numeric value.");
            var numericText = PlainText(number.Groups[1].Value);
            Assert.True(long.TryParse(numericText, NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out var rendered), "Dashboard tile values must be numeric.");
            Assert.Equal(value, rendered);
        }
    }

    public static IEnumerable<object[]> PostFormContracts()
    {
        yield return ["/Admin/Products", "Toggle", new[] { "id", "enabled" }];
        yield return [ProductEdit, "", new[] { "Id", "Input.Name", "Input.LicensePrefix" }];
        yield return ["/Admin/Products/Edit", "", new[] { "Id", "Input.Name", "Input.LicensePrefix" }];
        yield return ["/Admin/Batches", "DisableAll", new[] { "id", "reason" }];
        yield return ["/Admin/Batches/Create", "", new[] { "Input.ProductId", "Input.Name", "Input.Count", "Input.ValidityKind", "Input.DurationUnits", "Input.Note" }];
        yield return [LicenseDetails, "Toggle", new[] { "enabled", "Action.Reason" }];
        yield return [LicenseDetails, "Extend", new[] { "Extension.ValidityKind", "Extension.Units", "Extension.Reason" }];
        yield return [LicenseDetails, "Unbind", new[] { "bindingId", "Action.Reason" }];
        yield return ["/Admin/Settings", "", new[] { "Input.SystemName", "Input.Logo" }];
    }

    [Theory]
    [MemberData(nameof(PostFormContracts))]
    public async Task Management_forms_preserve_handler_names_fields_and_antiforgery(string path, string handler, string[] fields)
    {
        await using var host = await AdminPageTestHost.StartAsync();
        await host.AuthenticateAsync();
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var matchingForms = Forms(html).Where(form => IsPost(form) && FormHandler(form) == handler
            && NormalizePath(Attribute(form.OpeningTag, "action")) != "/Account/Logout").ToArray();
        Assert.True(matchingForms.Length > 0, "The expected management handler form must be rendered.");
        foreach (var form in matchingForms)
        {
            AssertFormToken(form);
            foreach (var field in fields)
            {
                Assert.True(Controls(form.Body).Any(tag => Attribute(tag, "name") == field), "A required model-bound management field is missing: " + field);
            }

            if (path == "/Admin/Settings")
            {
                Assert.Equal("multipart/form-data", Attribute(form.OpeningTag, "enctype"));
                var logo = Controls(form.Body).Single(tag => Attribute(tag, "name") == "Input.Logo");
                Assert.Equal("file", Attribute(logo, "type"));
                Assert.Equal("image/png,image/jpeg", Attribute(logo, "accept"));
            }
        }

        var logout = Forms(html).Where(form => NormalizePath(Attribute(form.OpeningTag, "action")) == "/Account/Logout").ToArray();
        Assert.True(logout.Length == 1 && IsPost(logout[0]), "The authenticated shell must keep a POST logout form.");
        AssertFormToken(logout[0]);
    }

    [Theory]
    [InlineData("/Admin/Licenses", "query", "productId", "status")]
    [InlineData("/Admin/Audit", "action", null, null)]
    public async Task Search_forms_keep_get_filter_contracts(string path, string first, string? second, string? third)
    {
        await using var host = await AdminPageTestHost.StartAsync();
        await host.AuthenticateAsync();
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forms = Forms(await response.Content.ReadAsStringAsync()).Where(form =>
            string.Equals(Attribute(form.OpeningTag, "method"), "get", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(forms.Length == 1, "Search filters must remain GET forms.");
        foreach (var name in new[] { first, second, third }.OfType<string>())
        {
            Assert.True(Controls(forms[0].Body).Any(tag => Attribute(tag, "name") == name), "A query-string filter field is missing: " + name);
        }
    }

    public static IEnumerable<object[]> MissingTokenPosts()
    {
        yield return ["/Account/Login"];
        yield return ["/Account/Logout"];
        foreach (var contract in PostFormContracts())
        {
            var path = (string)contract[0];
            var handler = (string)contract[1];
            yield return [path + (handler.Length == 0 ? "" : "?handler=" + handler)];
        }
    }

    [Theory]
    [MemberData(nameof(MissingTokenPosts))]
    public async Task Missing_antiforgery_token_rejects_posts_before_any_service_call(string path)
    {
        await using var host = await AdminPageTestHost.StartAsync();
        await host.AuthenticateAsync();
        using var response = await host.Client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Data.MutationCalls);
        Assert.Equal(0, host.Data.AuthenticationCalls);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Login_preserves_hidden_return_url_autocomplete_validation_and_real_antiforgery()
    {
        await using var host = await AdminPageTestHost.StartAsync();
        const string returnUrl = "/Admin/Licenses?status=ACTIVE&pageNumber=2";
        using var response = await host.Client.GetAsync("/Account/Login?ReturnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var form = Forms(html).Single(IsPost);
        AssertFormToken(form);
        var controls = Controls(form.Body);
        var hiddenReturn = controls.Single(tag => Attribute(tag, "name") == "ReturnUrl");
        Assert.Equal("hidden", Attribute(hiddenReturn, "type"));
        Assert.Equal(returnUrl, Attribute(hiddenReturn, "value"));
        var userName = controls.Single(tag => Attribute(tag, "name") == "Input.UserName");
        var password = controls.Single(tag => Attribute(tag, "name") == "Input.Password");
        Assert.Equal("username", Attribute(userName, "autocomplete"));
        Assert.Equal("current-password", Attribute(password, "autocomplete"));
        Assert.Equal("password", Attribute(password, "type"));
        Assert.True(Attribute(userName, "data-val-required").Length > 0, "Login username requires unobtrusive validation metadata.");
        Assert.True(Attribute(password, "data-val-required").Length > 0, "Login password requires unobtrusive validation metadata.");
        Assert.True(Tags(form.Body, "span").Any(tag => Attribute(tag, "data-valmsg-for") == "Input.UserName"), "Username needs a validation outlet.");
        Assert.True(Tags(form.Body, "span").Any(tag => Attribute(tag, "data-valmsg-for") == "Input.Password"), "Password needs a validation outlet.");

        // Only empty credentials are posted. Tokens and cookie values remain in memory and never
        // become assertion arguments, snapshots, fixture files, or test output.
        var token = Attribute(controls.Single(tag => Attribute(tag, "name") == "__RequestVerificationToken"), "value");
        var cookieValues = response.Headers.TryGetValues("Set-Cookie", out var headers) ? headers : [];
        var antiforgeryCookie = cookieValues.Where(value => value.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))
            .Select(value => value.Split(';', 2)[0]).ToArray();
        Assert.True(antiforgeryCookie.Length == 1, "The login form must issue a real antiforgery cookie.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["ReturnUrl"] = returnUrl,
                ["Input.UserName"] = string.Empty,
                ["Input.Password"] = string.Empty
            })
        };
        request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie[0]);
        using var invalid = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.True(Matches(invalidHtml, "<span[^>]+data-valmsg-for=[\"']Input\\.(UserName|Password)[\"'][^>]*>[^<]+</span>").Count == 2,
            "Server validation must render both required-field messages.");
        Assert.Equal(0, host.Data.AuthenticationCalls);
        AssertSecurityHeaders(invalid);

        // A generated, synthetic value exercises the model-only rejection summary. It is never
        // retained by the fake, echoed into assertions, or written to a process/log/artifact.
        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Post, "/Account/Login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["ReturnUrl"] = returnUrl,
                ["Input.UserName"] = "fixture-unknown",
                ["Input.Password"] = Guid.NewGuid().ToString("N")
            })
        };
        rejectedRequest.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie[0]);
        using var rejected = await host.Client.SendAsync(rejectedRequest);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var rejectedHtml = await rejected.Content.ReadAsStringAsync();
        Assert.True(Tags(rejectedHtml, "div").Any(tag => Attribute(tag, "role") == "alert"
            && Attribute(tag, "class").Contains("validation-summary-errors", StringComparison.Ordinal)),
            "Authentication rejection must use the visible model-only validation summary.");
        var rejectedPassword = Controls(rejectedHtml).Single(tag => Attribute(tag, "name") == "Input.Password");
        Assert.True(Attribute(rejectedPassword, "value").Length == 0, "A rejected password must not be rendered back into HTML.");
        Assert.Equal(1, host.Data.AuthenticationCalls);
    }

    [Theory]
    [InlineData("/Account/Login", false)]
    [InlineData("/Admin/Dashboard", true)]
    public async Task Plain_http_keeps_the_safety_warning_while_https_omits_it(string path, bool authenticate)
    {
        await using var host = await AdminPageTestHost.StartAsync();
        if (authenticate)
        {
            await host.AuthenticateAsync();
        }

        foreach (var scheme in new[] { "http", "https" })
        {
            using var response = await host.Client.GetAsync(scheme + "://localhost" + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var text = PlainText(html);
            Assert.True(text.Contains("当前为公网明文 HTTP 模式。", StringComparison.Ordinal) == (scheme == "http"),
                "The HTTP safety warning must depend on the actual request scheme.");
            Assert.True(text.Contains("licensectl enable-https", StringComparison.Ordinal) == (scheme == "http"),
                "Only the HTTP warning should offer the HTTPS switch command.");
            if (scheme == "http")
            {
                Assert.True(text.Contains("登录密码、会话和激活卡密可能被网络监听", StringComparison.Ordinal), "The preserved warning must explain the interception risk.");
                Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            }

            AssertSecurityHeaders(response);
        }
    }

    [Theory]
    [InlineData("/Account/Login", false)]
    [InlineData("/Admin/Dashboard", true)]
    public async Task Rendered_styles_and_scripts_are_local_resolvable_and_receive_security_headers(string path, bool authenticate)
    {
        await using var host = await AdminPageTestHost.StartAsync();
        if (authenticate)
        {
            await host.AuthenticateAsync();
        }

        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var resources = Tags(html, "link").Where(tag => Attribute(tag, "rel") == "stylesheet")
            .Select(tag => Attribute(tag, "href"))
            .Concat(Tags(html, "script").Select(tag => Attribute(tag, "src")).Where(src => src.Length > 0))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(resources.Length >= 4, "The actual shell must reference its local styles and scripts.");
        Assert.True(resources.Any(resource => resource.StartsWith("/css/site", StringComparison.Ordinal)), "The application stylesheet must be rendered.");
        foreach (var resource in resources)
        {
            Assert.True(resource.StartsWith('/') && !resource.StartsWith("//", StringComparison.Ordinal), "Frontend dependencies must be same-origin local references.");
            using var asset = await host.Client.GetAsync(resource);
            Assert.True(asset.StatusCode == HttpStatusCode.OK, "Referenced local asset unavailable: " + resource.Split('?', 2)[0]);
            Assert.True((await asset.Content.ReadAsByteArrayAsync()).Length > 0, "A referenced local asset must not be empty.");
            AssertSecurityHeaders(asset);
        }
    }

    [Theory]
    [InlineData(AdminPageDataState.Empty, HttpStatusCode.NotFound)]
    [InlineData(AdminPageDataState.Populated, HttpStatusCode.OK)]
    [InlineData(AdminPageDataState.LongText, HttpStatusCode.OK)]
    public async Task Fixture_brand_logo_states_use_the_real_local_page(AdminPageDataState state, HttpStatusCode expected)
    {
        await using var host = await AdminPageTestHost.StartAsync(state);
        using var response = await host.Client.GetAsync("/brand/logo");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0, "The configured synthetic logo must be served.");
        }

        AssertSecurityHeaders(response);
    }

    private static void AssertSuppliedContent(AdminPageTestHost host, string path, string html)
    {
        var text = PlainText(html);
        string? expected = path switch
        {
            "/Admin/Dashboard" or "/Admin/Audit" => "其他操作",
            "/Admin/Products" or "/Admin/Batches/Create" or "/Admin/Licenses" => host.Data.ProductName,
            "/Admin/Batches" => host.Data.BatchName,
            "/Admin/Settings" => "instance-fixture-local",
            _ when path == LicenseDetails => AdminPageFixtureData.BindingPublicId,
            _ => null
        };
        if (expected is not null)
        {
            Assert.True(text.Contains(expected, StringComparison.Ordinal), "The page must display its supplied service data: " + path);
        }

        if (path == ProductEdit)
        {
            var name = Controls(html).Single(tag => Attribute(tag, "name") == "Input.Name");
            Assert.Equal(host.Data.ProductName, Attribute(name, "value"));
        }
    }

    private static bool IsDataTablePage(string path) => path is "/Admin/Dashboard" or "/Admin/Products" or "/Admin/Batches"
        or "/Admin/Licenses" or "/Admin/Audit" || path == LicenseDetails;

    private static bool HasLongFixtureContent(string path) => path is "/Admin/Products" or "/Admin/Batches"
        or "/Admin/Batches/Create" or "/Admin/Licenses" || path == ProductEdit || path == LicenseDetails;

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", string.Join("", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", string.Join("", response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", string.Join("", response.Headers.GetValues("Referrer-Policy")));
        var permissions = string.Join("", response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("camera=()", permissions);
        Assert.Contains("microphone=()", permissions);
        var csp = string.Join("", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("form-action 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    private static void AssertAccessibleFormLabelsAndUniqueIds(string html, string path)
    {
        // Inspect opening tags only, never include token-bearing HTML/tags in assertion output.
        var ids = Matches(html, "<[a-z][a-z0-9:-]*\\b[^>]*>")
            .Select(match => Attribute(match.Value, "id")).Where(id => id.Length > 0).ToArray();
        Assert.True(ids.Distinct(StringComparer.Ordinal).Count() == ids.Length,
            "Rendered element IDs must be unique across the entire page: " + path);
        var labelTargets = Tags(html, "label").Select(tag => Attribute(tag, "for")).ToArray();
        foreach (var control in Controls(html).Where(tag =>
            !string.Equals(Attribute(tag, "type"), "hidden", StringComparison.OrdinalIgnoreCase)))
        {
            var id = Attribute(control, "id");
            Assert.True(id.Length > 0, "Every nonhidden input, select, and textarea needs an ID for its label: " + path);
            Assert.True(labelTargets.Count(target => string.Equals(target, id, StringComparison.Ordinal)) == 1,
                "Every nonhidden input, select, and textarea needs exactly one associated label: " + path);
        }
    }

    private static void AssertPostFormsHaveAntiforgery(string html)
    {
        var postForms = Forms(html).Where(IsPost).ToArray();
        Assert.True(postForms.Length > 0, "The authenticated shell must retain its logout form.");
        foreach (var form in postForms)
        {
            AssertFormToken(form);
        }
    }

    private static void AssertFormToken(HtmlForm form)
    {
        var tokens = Controls(form.Body).Where(tag => Attribute(tag, "name") == "__RequestVerificationToken").ToArray();
        Assert.True(tokens.Length == 1, "Every POST form must contain exactly one antiforgery field.");
        Assert.True(Attribute(tokens[0], "type") == "hidden" && Attribute(tokens[0], "value").Length > 0,
            "POST forms require a populated hidden antiforgery field.");
    }

    // These narrow HTML-contract helpers deliberately use boolean/small-value assertions, not a DOM
    // snapshot or Assert.Contains(expected, fullHtml), so failures cannot print antiforgery tickets.
    private sealed record HtmlForm(string OpeningTag, string Body);
    private static IReadOnlyList<HtmlForm> Forms(string html) => Matches(html, "(<form\\b[^>]*>)(.*?)</form>")
        .Select(match => new HtmlForm(match.Groups[1].Value, match.Groups[2].Value)).ToArray();
    private static bool IsPost(HtmlForm form) => string.Equals(Attribute(form.OpeningTag, "method"), "post", StringComparison.OrdinalIgnoreCase);
    private static string FormHandler(HtmlForm form)
    {
        var action = Attribute(form.OpeningTag, "action");
        var handler = Regex.Match(action, "[?&]handler=([^&]*)", RegexOptions.IgnoreCase, RegexTimeout);
        return handler.Success ? WebUtility.UrlDecode(handler.Groups[1].Value) : string.Empty;
    }

    private static IReadOnlyList<string> Controls(string html) => Matches(html, "<(?:input|select|textarea)\\b[^>]*>").Select(match => match.Value).ToArray();
    private static IReadOnlyList<string> Tags(string html, string name) => Matches(html, "<" + Regex.Escape(name) + "\\b[^>]*>").Select(match => match.Value).ToArray();
    private static MatchCollection Matches(string html, string pattern) => Regex.Matches(html, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
    private static string Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, "(?:^|\\s)" + Regex.Escape(name) + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))",
            RegexOptions.IgnoreCase, RegexTimeout);
        return match.Success ? WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value) : string.Empty;
    }

    private static string NormalizePath(string path)
    {
        var result = path.Split('?', 2)[0].TrimEnd('/');
        return result.EndsWith("/Index", StringComparison.OrdinalIgnoreCase) ? result[..^6] : result;
    }

    private static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", "", RegexOptions.Singleline, RegexTimeout)).Trim();
}
