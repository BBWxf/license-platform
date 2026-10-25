using System.Text.Json;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Web.Services;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AuditPresentationFormatterTests
{
    private const string AdministratorId = "10000000-0000-0000-0000-000000000021";
    private static readonly (string Action, string Target)[] ProducerActions =
    [
        ("ADMIN.INITIALIZED", "Administrator"),
        ("ADMIN.PASSWORD_RESET", "Administrator"),
        ("ADMIN.LOGIN_FAILED", "Administrator"),
        ("ADMIN.LOGIN_LOCKED", "Administrator"),
        ("ADMIN.LOGIN_SUCCEEDED", "Administrator"),
        ("PRODUCT.CREATE", "Product"),
        ("PRODUCT.UPDATE", "Product"),
        ("PRODUCT.ENABLE", "Product"),
        ("PRODUCT.DISABLE", "Product"),
        ("PRODUCT.DELETE", "Product"),
        ("BATCH.CREATE", "LicenseBatch"),
        ("BATCH.EXPORT_DOWNLOAD", "LicenseBatch"),
        ("BATCH.DISABLE_ALL", "LicenseBatch"),
        ("LICENSE.ENABLE", "LicenseKey"),
        ("LICENSE.DISABLE", "LicenseKey"),
        ("LICENSE.EXTEND", "LicenseKey"),
        ("LICENSE.DELETE", "LicenseKey"),
        ("DEVICE.UNBIND", "DeviceBinding"),
        ("SETTINGS.UPDATE", "ApplicationSetting"),
        ("license.activated", "LicenseKey"),
        ("lease.reissued", "LicenseKey"),
        ("lease.refreshed", "LicenseKey")
    ];

    [Fact]
    public void Every_actual_producer_action_has_a_Chinese_choice_and_the_same_shared_label()
    {
        Assert.Equal(ProducerActions.Length, AuditPresentationFormatter.Actions.Count);
        Assert.True(ProducerActions.Select(item => item.Action).OrderBy(code => code, StringComparer.Ordinal)
            .SequenceEqual(AuditPresentationFormatter.Actions.Select(item => item.Code).OrderBy(code => code, StringComparer.Ordinal)),
            "The selectable raw event codes must match all actual producer events.");
        foreach (var (action, target) in ProducerActions)
        {
            var choice = AuditPresentationFormatter.Actions.Single(item => item.Code == action);
            var presentation = AuditPresentationFormatter.Format(action, target);
            Assert.True(HasChinese(choice.Label) && HasChinese(choice.Group), "Event choices need Chinese labels and groups.");
            Assert.True(presentation.ActionLabel == choice.Label && presentation.ActionLabel != "其他操作",
                "Each known event must use its registered Chinese label.");
            Assert.True(AuditPresentationFormatter.Format(action.ToLowerInvariant(), target).ActionLabel == choice.Label,
                "Historical casing variations must retain the known event label.");
            Assert.True(HasChinese(presentation.TargetLabel) && !string.IsNullOrWhiteSpace(presentation.Description),
                "Known events need a readable object type and a safe basic explanation even without JSON.");
            AssertBounded(presentation);
        }
    }

    [Theory]
    [InlineData("Day", 2, "2 天")]
    [InlineData("Month", 2, "每月 30 天")]
    [InlineData("Year", 2, "每年 365 天")]
    [InlineData("Permanent", 0, "永久")]
    [InlineData("Unrecognized", 2, "无法识别")]
    public void Batch_summaries_use_typed_count_and_explicit_fixed_duration_semantics(string kind, int units, string expectedTerm)
    {
        var presentation = AuditPresentationFormatter.Format("BATCH.CREATE", "LicenseBatch",
            JsonSerializer.Serialize(new { Count = 17, validity = kind, DurationUnits = units }));

        Assert.True(presentation.Description.Contains("17", StringComparison.Ordinal), "The generated count must be readable.");
        Assert.True(Text(presentation).Contains(expectedTerm, StringComparison.Ordinal), "The recorded validity must use the correct duration semantics.");
        if (kind == "Unrecognized")
        {
            Assert.False(presentation.Details.Any(detail => detail.Value == "永久"), "An unknown validity must not default to permanent.");
        }
        AssertBounded(presentation);
    }

    [Fact]
    public void Invalid_duration_codes_are_not_repaired_into_valid_or_permanent_terms()
    {
        foreach (var kind in new[] { "Permanent ", string.Concat("Perma", (char)0x200B, "nent"), "Day\n", "day" })
        {
            var batch = AuditPresentationFormatter.Format("BATCH.CREATE", "LicenseBatch",
                JsonSerializer.Serialize(new { Count = 2, validity = kind, DurationUnits = 3 }));
            var extension = AuditPresentationFormatter.Format("LICENSE.EXTEND", "LicenseKey",
                JsonSerializer.Serialize(new { kind, units = 3 }));
            Assert.True(batch.Details.Any(detail => detail.Value == "未记录或无法识别"),
                "An invalid duration code must not be normalized into a valid or permanent card type.");
            Assert.Empty(extension.Details);
        }
    }

    [Fact]
    public void Administrator_initialization_does_not_claim_a_manually_executed_command()
    {
        var presentation = AuditPresentationFormatter.Format("ADMIN.INITIALIZED", "Administrator",
            "{\"userName\":\"public-admin-snapshot\"}", AuditActorType.System);
        Assert.False(presentation.Description.Contains("命令", StringComparison.Ordinal),
            "Initialization may come from the hosted bootstrap service or a local command.");
        Assert.True(AuditPresentationFormatter.SourceLabel("local-cli") == "本机管理",
            "The shared historical source marker must not claim a manual CLI invocation.");
    }

    [Fact]
    public void Whitelisted_snapshots_counts_binding_versions_and_typed_logo_changes_are_readable()
    {
        var product = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product",
            "{\"Name\":\"Public snapshot name\",\"Code\":\"public_product_code\",\"LicensePrefix\":\"DEMO\"}");
        var initialized = AuditPresentationFormatter.Format("ADMIN.INITIALIZED", "Administrator", "{\"userName\":\"public-admin-snapshot\"}");
        var reset = AuditPresentationFormatter.Format("ADMIN.PASSWORD_RESET", "Administrator", "{\"source\":\"local-cli\"}");
        var disabled = AuditPresentationFormatter.Format("BATCH.DISABLE_ALL", "LicenseBatch", "{\"count\":37}");
        var unbound = AuditPresentationFormatter.Format("DEVICE.UNBIND", "DeviceBinding",
            "{\"licenseId\":\"40000000-0000-0000-0000-000000000021\",\"bindingVersion\":9}");
        var settings = AuditPresentationFormatter.Format("SETTINGS.UPDATE", "ApplicationSetting", "{\"systemName\":\"Public system snapshot\",\"logoUpdated\":false}");
        Assert.True(Text(product).Contains("Public snapshot name", StringComparison.Ordinal)
            && Text(product).Contains("public_product_code", StringComparison.Ordinal), "Public product snapshots may be shown.");
        Assert.True(Text(initialized).Contains("public-admin-snapshot", StringComparison.Ordinal), "An existing initialization name snapshot may be shown.");
        Assert.True(Text(reset).Contains("本机命令行", StringComparison.Ordinal), "The recorded local CLI source should be understandable.");
        Assert.True(disabled.Description.Contains("37", StringComparison.Ordinal), "The actual whole-batch count must be shown.");
        Assert.True(unbound.Details.Any(detail => detail.Value == "9"), "Unbinding must retain its structured binding version.");
        Assert.True(Text(settings).Contains("Public system snapshot", StringComparison.Ordinal)
            && Text(settings).Contains("未上传", StringComparison.Ordinal), "A false logo flag must not be treated as a new upload.");
        foreach (var action in new[] { "license.activated", "lease.reissued", "lease.refreshed" })
        {
            var lease = AuditPresentationFormatter.Format(action, "LicenseKey", "{\"bindingVersion\":11}");
            Assert.True(lease.Details.Any(detail => detail.Value == "11"), "Client lease events must show the typed binding version.");
        }
    }

    [Theory]
    [InlineData("ADMIN.LOGIN_FAILED")]
    [InlineData("ADMIN.LOGIN_LOCKED")]
    public void Rejected_logins_identify_an_associated_attempted_account_not_an_authenticated_operator(string action)
    {
        var presentation = AuditPresentationFormatter.Format(action, "Administrator", "{\"result\":\"recorded\"}",
            AuditActorType.Administrator, AdministratorId);
        var withoutResult = AuditPresentationFormatter.Format(action, "Administrator", actorType: AuditActorType.Administrator, actorId: AdministratorId);
        var unknown = AuditPresentationFormatter.Format(action, "Administrator", actorType: AuditActorType.Administrator);
        var authenticated = AuditPresentationFormatter.Format("ADMIN.LOGIN_SUCCEEDED", "Administrator", actorType: AuditActorType.Administrator, actorId: AdministratorId);

        Assert.True(presentation.ActorLabel.Contains("关联", StringComparison.Ordinal)
            && presentation.ActorLabel.Contains("未验证", StringComparison.Ordinal), "A failed or locked login is only associated with the attempted account.");
        Assert.True(presentation.ActorLabel != authenticated.ActorLabel, "A rejected login must not inherit the authenticated actor label.");
        Assert.True(unknown.ActorLabel.Contains("未识别", StringComparison.Ordinal), "An unknown attempted account must remain unknown.");
        Assert.False(Text(presentation).Contains("成功", StringComparison.Ordinal), "Recorded login failures must not become successful operations.");
        Assert.True(Text(presentation) == Text(withoutResult), "The producer's recorded marker carries no success outcome.");
    }

    [Fact]
    public void Export_preparation_and_reissued_leases_do_not_claim_a_saved_file_or_first_activation()
    {
        var repeatable = AuditPresentationFormatter.Format("BATCH.EXPORT_DOWNLOAD", "LicenseBatch", "{\"repeatable\":true}");
        var legacy = AuditPresentationFormatter.Format("BATCH.EXPORT_DOWNLOAD", "LicenseBatch", "{\"once\":true}");
        Assert.True(repeatable.Description == legacy.Description && repeatable.Description.Contains("准备", StringComparison.Ordinal),
            "Both export modes describe preparing the file, not a first or repeated browser attempt.");
        Assert.True(repeatable.Description.Contains("不代表浏览器已保存", StringComparison.Ordinal), "Preparing an export must not claim the browser saved it.");
        Assert.True(repeatable.Details.Any(detail => detail.Value.Contains("可重复", StringComparison.Ordinal))
            && legacy.Details.Any(detail => detail.Value.Contains("旧版一次性", StringComparison.Ordinal)), "Flags describe historical export mode only.");
        Assert.False(repeatable.Details.Concat(legacy.Details).Any(detail => detail.Value.Contains("首次", StringComparison.Ordinal)
            || detail.Value.Contains("再次", StringComparison.Ordinal)), "Export mode must not be interpreted as request attempt count.");
        var reissued = AuditPresentationFormatter.Format("lease.reissued", "LicenseKey", "{\"bindingVersion\":2}");
        var activated = AuditPresentationFormatter.Format("license.activated", "LicenseKey");
        Assert.True(reissued.ActionLabel != activated.ActionLabel && reissued.Description.Contains("已激活", StringComparison.Ordinal)
            && reissued.Description.Contains("不是首次激活", StringComparison.Ordinal), "Lease reissue is distinct from first activation.");
    }

    [Fact]
    public void Null_expiry_and_unknown_extension_kind_remain_uncertain_while_valid_dates_are_UTC()
    {
        var uncertain = AuditPresentationFormatter.Format("LICENSE.EXTEND", "LicenseKey",
            "{\"kind\":\"Unrecognized\",\"units\":2,\"beforeExpiresAtUtc\":null,\"afterExpiresAtUtc\":null}");
        Assert.Equal(2, uncertain.Details.Count);
        Assert.True(uncertain.Details.All(detail => detail.Value.Contains("不表示永久", StringComparison.Ordinal)),
            "Null expiry means unrecorded or undetermined, not a permanent license.");
        Assert.False(uncertain.Details.Any(detail => detail.Value == "永久"), "Unknown extension kinds must not default to permanent.");
        var dated = AuditPresentationFormatter.Format("LICENSE.EXTEND", "LicenseKey",
            "{\"kind\":\"Day\",\"units\":3,\"beforeExpiresAtUtc\":\"2026-10-22T08:30:00+08:00\",\"afterExpiresAtUtc\":\"invalid-date\"}");
        Assert.True(dated.Details.Any(detail => detail.Value == "2026-10-22 00:30:00 UTC"), "An offset-bearing date must be normalized to UTC.");
        Assert.False(Text(dated).Contains("invalid-date", StringComparison.Ordinal), "Invalid dates must not be displayed as expiry values.");
    }

    [Fact]
    public void Malformed_nonobject_duplicate_and_wrongly_typed_payloads_fall_back_without_echoing_values()
    {
        string?[] invalid = [null, "", " ", "{", "[]", "null", "42", "true", "\"scalar\"",
            "{\"Name\":[\"wrong-shape\"]}", "{\"Name\":{\"nested\":\"wrong-shape\"}}",
            "{\"Name\":\"duplicate-left\",\"Name\":\"duplicate-right\"}"];
        var basic = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product");
        foreach (var json in invalid)
        {
            var presentation = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", json);
            Assert.True(Text(presentation) == Text(basic), "Unusable JSON must retain a safe basic presentation, not raw payload values.");
            AssertBounded(presentation);
        }
        var wrongTypes = AuditPresentationFormatter.Format("BATCH.CREATE", "LicenseBatch",
            "{\"Count\":\"817\",\"validity\":[\"Permanent\"],\"DurationUnits\":true,\"productId\":123}");
        Assert.False(Text(wrongTypes).Contains("817", StringComparison.Ordinal)
            || wrongTypes.Details.Any(detail => detail.Value == "永久"), "Wrong JSON types must not be coerced into counts or validity.");
        var stringBoolean = AuditPresentationFormatter.Format("SETTINGS.UPDATE", "ApplicationSetting", "{\"logoUpdated\":\"true\"}");
        Assert.Empty(stringBoolean.Details);
    }

    [Fact]
    public void Json_size_depth_and_public_text_limits_are_enforced()
    {
        const string marker = "bounded-public-snapshot";
        const string prefix = "{\"Name\":\"" + marker + "\",\"padding\":\"";
        var atLimit = prefix + new string('x', 16 * 1024 - prefix.Length - 2) + "\"}";
        Assert.True(Text(AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", atLimit)).Contains(marker, StringComparison.Ordinal),
            "An otherwise safe object exactly at the 16K character boundary should remain usable.");
        var tooDeep = "{\"Name\":\"" + marker + "\",\"nested\":" + new string('[', 9) + "0" + new string(']', 9) + "}";
        foreach (var json in new[] { atLimit + " ", tooDeep })
        {
            var presentation = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", json);
            Assert.False(Text(presentation).Contains(marker, StringComparison.Ordinal), "Oversized or overly deep JSON must not be parsed for display.");
            AssertBounded(presentation);
        }
        var longName = "Public-<snapshot-probe>&" + new string('W', 500);
        var bounded = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", JsonSerializer.Serialize(new { Name = longName }));
        Assert.True(bounded.Details.Any(detail => detail.Value.StartsWith("Public-<snapshot-probe>&", StringComparison.Ordinal)),
            "Allowed public names remain text for Razor to encode.");
        Assert.False(Text(bounded).Contains(longName, StringComparison.Ordinal), "A public snapshot must not bypass the single-value limit.");
        AssertBounded(bounded);
    }

    [Fact]
    public void Secrets_unknown_fields_free_text_and_raw_device_identifiers_are_never_presentation_details()
    {
        var sensitiveMarker = "synthetic-sensitive-" + Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new
        {
            Name = "Public snapshot",
            note = sensitiveMarker,
            reason = sensitiveMarker,
            deviceId = sensitiveMarker,
            fingerprint = sensitiveMarker,
            hardwareFingerprint = sensitiveMarker,
            licenseKey = sensitiveMarker,
            password = sensitiveMarker,
            cookie = sensitiveMarker,
            token = sensitiveMarker,
            privateKey = sensitiveMarker,
            connectionString = sensitiveMarker,
            unknown = new { value = sensitiveMarker },
            result = "recorded"
        });
        foreach (var (action, target) in ProducerActions.Append(("FIXTURE.UNKNOWN", "UnknownType")))
        {
            var presentation = AuditPresentationFormatter.Format(action, target, payload);
            Assert.False(Text(presentation).Contains(sensitiveMarker, StringComparison.Ordinal),
                "No raw secret, unknown property, free-form reason/note, or device identifier may enter the presentation.");
            AssertBounded(presentation);
        }
        var unknown = AuditPresentationFormatter.Format("FIXTURE.UNKNOWN", "UnknownType", payload);
        Assert.True(unknown.ActionLabel == "其他操作" && unknown.TargetLabel == "其他对象", "Unknown events need neutral Chinese fallback labels.");
        Assert.False(Text(unknown).Contains("Public snapshot", StringComparison.Ordinal), "Unknown action payloads must not acquire a permissive field whitelist.");
    }

    [Fact]
    public void Legacy_dto_fallbacks_never_parse_summary_or_interpret_result_and_attached_presentations_are_reused()
    {
        var marker = "legacy-summary-" + Guid.NewGuid().ToString("N");
        var summary = JsonSerializer.Serialize(new { Name = marker });
        var row = new AuditListItem(AdminPageFixtureData.Timestamp, "Administrator", "PRODUCT.UPDATE", "Product", null,
            "SUCCESS", "192.0.2.10", summary);
        var recent = new RecentAuditItem(AdminPageFixtureData.Timestamp, "PRODUCT.UPDATE", "Product", "FAILURE", summary);
        Assert.True(Text(AuditPresentationFormatter.For(row)) == Text(AuditPresentationFormatter.For(row with { Summary = null, Result = "RECORDED" }))
            && Text(AuditPresentationFormatter.For(recent)) == Text(AuditPresentationFormatter.For(recent with { Summary = null, Result = "RECORDED" })),
            "Legacy summaries and raw result codes must not influence the safe fallback.");
        Assert.False(Text(AuditPresentationFormatter.For(row)).Contains(marker, StringComparison.Ordinal)
            || Text(AuditPresentationFormatter.For(recent)).Contains(marker, StringComparison.Ordinal), "Legacy Summary is never a source of display JSON.");
        var supplied = AuditPresentationFormatter.Format("PRODUCT.UPDATE", "Product", "{\"Name\":\"Supplied snapshot\"}");
        Assert.Same(supplied, AuditPresentationFormatter.For(row with { Presentation = supplied }));
        Assert.Same(supplied, AuditPresentationFormatter.For(recent with { Presentation = supplied }));
    }

    [Fact]
    public void Identifier_and_source_helpers_only_expose_recognized_public_forms()
    {
        Assert.True(AuditPresentationFormatter.ShortIdentifier(AdministratorId).Length < AdministratorId.Length,
            "Default labels must use a short identifier rather than a full GUID.");
        Assert.True(AuditPresentationFormatter.FullIdentifier(AdministratorId) == AdministratorId, "A full GUID remains available for technical details.");
        Assert.True(AuditPresentationFormatter.SourceLabel("local-cli").Contains("本机", StringComparison.Ordinal)
            && AuditPresentationFormatter.SourceLabel("192.0.2.10") == "192.0.2.10", "Recognized sources retain safe, readable forms.");
        var unexpected = "synthetic-untrusted-" + Guid.NewGuid().ToString("N");
        foreach (var value in new string?[] { null, "", "-", unexpected, new string('x', 500) })
        {
            var labels = new[] { AuditPresentationFormatter.ShortIdentifier(value), AuditPresentationFormatter.FullIdentifier(value), AuditPresentationFormatter.SourceLabel(value) };
            Assert.True(labels.All(label => !string.IsNullOrWhiteSpace(label) && label.Length <= 120), "Missing and nonstandard identifiers need bounded safe fallbacks.");
            Assert.False(labels.Any(label => label.Contains(unexpected, StringComparison.Ordinal)), "Arbitrary identifier/source strings must not become a secret-display escape hatch.");
        }
    }

    private static bool HasChinese(string value) => value.Any(character => character is >= '一' and <= '鿿');
    private static string Text(AuditPresentation presentation) => string.Join("\n",
        new[] { presentation.ActionLabel, presentation.Description, presentation.TargetLabel, presentation.ActorLabel }
            .Concat(presentation.Details.SelectMany(detail => new[] { detail.Label, detail.Value })));
    private static void AssertBounded(AuditPresentation presentation)
    {
        Assert.True(presentation.Description.Length <= 280 && presentation.Details.Count <= 8,
            "Descriptions and structured details must remain bounded.");
        Assert.True(presentation.Details.All(detail => detail.Label.Length <= 120 && detail.Value.Length <= 120),
            "Each displayed detail must respect the single-value length limit.");
    }
}
