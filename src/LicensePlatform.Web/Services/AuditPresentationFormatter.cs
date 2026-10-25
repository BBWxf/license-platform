using System.Globalization;
using System.Net;
using System.Text.Json;
using LicensePlatform.Domain.Auditing;

namespace LicensePlatform.Web.Services;

public sealed record AuditActionOption(string Code, string Label, string Group);
public sealed record AuditPresentationDetail(string Label, string Value);
public sealed record AuditPresentation(
    string ActionLabel,
    string Description,
    string TargetLabel,
    string ActorLabel,
    IReadOnlyList<AuditPresentationDetail> Details);

public static class AuditPresentationFormatter
{
    private const int MaximumJsonLength = 16 * 1024;
    private const int MaximumTextLength = 120;
    private const int MaximumDetailCount = 8;

    public static IReadOnlyList<AuditActionOption> Actions { get; } = Array.AsReadOnly(new[]
    {
        new AuditActionOption("ADMIN.LOGIN_SUCCEEDED", "登录验证通过", "管理员与登录"),
        new AuditActionOption("ADMIN.LOGIN_FAILED", "登录验证失败", "管理员与登录"),
        new AuditActionOption("ADMIN.LOGIN_LOCKED", "账号锁定，登录被拒绝", "管理员与登录"),
        new AuditActionOption("ADMIN.INITIALIZED", "初始化管理员", "管理员与登录"),
        new AuditActionOption("ADMIN.PASSWORD_RESET", "本机重置管理员密码", "管理员与登录"),
        new AuditActionOption("PRODUCT.CREATE", "创建产品", "产品管理"),
        new AuditActionOption("PRODUCT.UPDATE", "更新产品", "产品管理"),
        new AuditActionOption("PRODUCT.ENABLE", "启用产品", "产品管理"),
        new AuditActionOption("PRODUCT.DISABLE", "停用产品", "产品管理"),
        new AuditActionOption("PRODUCT.DELETE", "删除产品", "产品管理"),
        new AuditActionOption("BATCH.CREATE", "生成卡密批次", "卡密批次"),
        new AuditActionOption("BATCH.EXPORT_DOWNLOAD", "准备导出卡密", "卡密批次"),
        new AuditActionOption("BATCH.DISABLE_ALL", "禁用整批卡密", "卡密批次"),
        new AuditActionOption("LICENSE.ENABLE", "恢复卡密使用", "卡密与设备"),
        new AuditActionOption("LICENSE.DISABLE", "禁用卡密", "卡密与设备"),
        new AuditActionOption("LICENSE.EXTEND", "延长卡密有效期", "卡密与设备"),
        new AuditActionOption("LICENSE.DELETE", "删除卡密", "卡密与设备"),
        new AuditActionOption("DEVICE.UNBIND", "解除设备绑定", "卡密与设备"),
        new AuditActionOption("license.activated", "首次激活卡密", "客户端授权"),
        new AuditActionOption("lease.reissued", "重新签发租约", "客户端授权"),
        new AuditActionOption("lease.refreshed", "续期在线租约", "客户端授权"),
        new AuditActionOption("SETTINGS.UPDATE", "更新系统设置", "系统设置")
    });

    public static AuditPresentation For(AuditListItem item) => item.Presentation
        ?? Format(item.Action, item.TargetType, actorType: ParseActorType(item.Actor), actorId: item.Actor, targetId: item.TargetId);

    public static AuditPresentation For(RecentAuditItem item) => item.Presentation
        ?? Format(item.Action, item.TargetType);

    public static AuditPresentation Format(
        string action,
        string targetType,
        string? dataJson = null,
        AuditActorType? actorType = null,
        string? actorId = null,
        string? targetId = null)
    {
        var code = action?.ToUpperInvariant() ?? string.Empty;
        var option = Actions.FirstOrDefault(item => string.Equals(item.Code, action, StringComparison.OrdinalIgnoreCase));
        var description = Describe(code);
        var details = new List<AuditPresentationDetail>();

        if (option is not null && !string.IsNullOrWhiteSpace(dataJson) && dataJson.Length <= MaximumJsonLength)
        {
            try
            {
                using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions { MaxDepth = 8 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && HasUniqueProperties(root))
                {
                    description = Explain(code, root, details, description);
                }
            }
            catch (JsonException)
            {
                // A historical or malformed payload must not prevent reading the audit list.
            }
        }

        return new AuditPresentation(
            option?.Label ?? "其他操作",
            Limit(description, 280),
            Guid.TryParse(targetId, out var targetGuid)
                ? $"{TargetLabel(targetType)} · {targetGuid.ToString("N")[..8]}"
                : TargetLabel(targetType),
            ActorLabel(code, actorType, actorId),
            details.AsReadOnly());
    }

    public static string ShortIdentifier(string? value)
    {
        if (Guid.TryParse(value, out var id))
        {
            return id.ToString("N")[..8];
        }

        return value switch
        {
            "branding" => "品牌配置",
            "unknown" => "未识别账号",
            null or "" or "-" => "未记录",
            "System" or "Administrator" or "LicenseClient" => "未记录",
            _ => "非标准标识"
        };
    }

    public static string FullIdentifier(string? value)
    {
        if (Guid.TryParse(value, out var id))
        {
            return id.ToString("D");
        }

        return value switch
        {
            "branding" => "branding（系统品牌配置）",
            "unknown" => "unknown（未识别账号）",
            null or "" or "-" or "System" or "Administrator" or "LicenseClient" => "未记录",
            _ => "非标准标识（已隐藏）"
        };
    }

    public static string SourceLabel(string? value)
    {
        if (string.Equals(value, "local-cli", StringComparison.OrdinalIgnoreCase))
        {
            return "本机管理";
        }

        if (string.IsNullOrWhiteSpace(value) || value == "-")
        {
            return "未记录";
        }

        return IPAddress.TryParse(value, out var address) ? address.ToString() : "其他来源";
    }

    private static string Describe(string code) => code switch
    {
        "ADMIN.INITIALIZED" => "创建了初始管理员账号。",
        "ADMIN.PASSWORD_RESET" => "通过本机管理命令重置了管理员密码。",
        "ADMIN.LOGIN_FAILED" => "此次登录未通过验证；关联账号不代表实际操作人。",
        "ADMIN.LOGIN_LOCKED" => "此次登录因账号已被锁定而被拒绝，未进行密码验证。",
        "ADMIN.LOGIN_SUCCEEDED" => "登录凭据已通过验证，不代表浏览器已收到登录会话。",
        "PRODUCT.CREATE" => "创建了产品配置。",
        "PRODUCT.UPDATE" => "更新了产品配置。",
        "PRODUCT.ENABLE" => "将产品设为启用状态。",
        "PRODUCT.DISABLE" => "将产品设为停用状态。",
        "PRODUCT.DELETE" => "永久删除了产品及其卡密、批次、导出存档和签名密钥，保留审计记录。",
        "BATCH.CREATE" => "生成了一批卡密；数量和有效期以已记录的信息为准。",
        "BATCH.EXPORT_DOWNLOAD" => "已准备卡密 CSV 导出文件，不代表浏览器已保存。",
        "BATCH.DISABLE_ALL" => "对批次中的卡密执行了整批禁用。",
        "LICENSE.ENABLE" => "恢复卡密为可用状态，不重置原有效期。",
        "LICENSE.DISABLE" => "将卡密设为禁用状态。",
        "LICENSE.DELETE" => "永久删除了卡密及其设备绑定和激活挑战，保留审计记录和原始批次导出快照。",
        "LICENSE.EXTEND" => "增加卡密的总有效期；未记录的到期时间不代表永久。",
        "DEVICE.UNBIND" => "解除设备绑定，保留卡密原有总有效期。",
        "SETTINGS.UPDATE" => "更新了系统品牌显示设置。",
        "LICENSE.ACTIVATED" => "为尚未激活的卡密完成首次激活并签发租约。",
        "LEASE.REISSUED" => "为已激活的卡密重新签发租约，不是首次激活。",
        "LEASE.REFRESHED" => "客户端续期在线租约，不改变卡密原有总有效期。",
        _ => "这是一条其他类型的操作记录，可展开查看事件代码和对象标识。"
    };

    private static string Explain(string code, JsonElement root, List<AuditPresentationDetail> details, string description)
    {
        switch (code)
        {
            case "ADMIN.INITIALIZED":
                AddText(details, root, "userName", "初始化账号名称");
                break;
            case "ADMIN.PASSWORD_RESET":
                if (ReadText(root, "source") == "local-cli")
                {
                    Add(details, "执行位置", "本机命令行");
                }
                break;
            case "PRODUCT.CREATE":
            case "PRODUCT.UPDATE":
                AddText(details, root, "Name", "记录中的产品名称");
                AddText(details, root, "Code", "产品编码");
                var name = ReadText(root, "Name");
                if (name is not null)
                {
                    description = $"{(code == "PRODUCT.CREATE" ? "创建" : "更新")}产品“{name}”。";
                }
                break;
            case "PRODUCT.DELETE":
                AddText(details, root, "Name", "记录中的产品名称");
                AddText(details, root, "Code", "产品编码");
                break;
            case "LICENSE.DELETE":
                AddGuid(details, root, "productId", "产品编号");
                AddGuid(details, root, "batchId", "原始批次编号");
                break;
            case "BATCH.CREATE":
                AddGuid(details, root, "productId", "产品编号");
                var count = ReadPositiveInteger(root, "Count", 10_000);
                var term = ReadTerm(root, "validity", "DurationUnits", allowPermanent: true);
                if (count.HasValue)
                {
                    Add(details, "生成数量", $"{count.Value.ToString(CultureInfo.InvariantCulture)} 张");
                    description = term is null
                        ? $"生成 {count.Value.ToString(CultureInfo.InvariantCulture)} 张卡密；卡型未记录或无法识别。"
                        : $"生成 {count.Value.ToString(CultureInfo.InvariantCulture)} 张卡密，有效期：{term}。";
                }
                Add(details, "卡密有效期", term ?? "未记录或无法识别");
                break;
            case "BATCH.EXPORT_DOWNLOAD":
                var repeatable = ReadBoolean(root, "repeatable");
                var once = ReadBoolean(root, "once");
                if (repeatable == true && once != true)
                {
                    Add(details, "当时的导出方式", "可重复导出");
                }
                else if (once == true && repeatable != true)
                {
                    Add(details, "当时的导出方式", "旧版一次性导出");
                }
                break;
            case "BATCH.DISABLE_ALL":
                var disabled = ReadPositiveInteger(root, "count");
                if (disabled.HasValue)
                {
                    Add(details, "处理数量", $"{disabled.Value.ToString(CultureInfo.InvariantCulture)} 张");
                    description = $"禁用了该批次中的 {disabled.Value.ToString(CultureInfo.InvariantCulture)} 张卡密。";
                }
                break;
            case "LICENSE.EXTEND":
                var extension = ReadTerm(root, "kind", "units", allowPermanent: false);
                if (extension is not null)
                {
                    Add(details, "增加的有效期", extension);
                    description = $"卡密总有效期增加 {extension}，不重置激活状态。";
                }
                AddDate(details, root, "beforeExpiresAtUtc", "变更前到期时间");
                AddDate(details, root, "afterExpiresAtUtc", "变更后到期时间");
                break;
            case "DEVICE.UNBIND":
                AddGuid(details, root, "licenseId", "关联卡密编号");
                AddVersion(details, root, "解绑后的绑定版本");
                break;
            case "SETTINGS.UPDATE":
                AddText(details, root, "systemName", "记录中的系统名称");
                var logoUpdated = ReadBoolean(root, "logoUpdated");
                if (logoUpdated.HasValue)
                {
                    Add(details, "Logo 上传", logoUpdated.Value ? "此次上传了新 Logo" : "此次未上传 Logo");
                }
                break;
            case "LICENSE.ACTIVATED":
            case "LEASE.REISSUED":
            case "LEASE.REFRESHED":
                AddGuid(details, root, "productId", "产品编号");
                AddVersion(details, root, "绑定版本");
                break;
        }

        return description;
    }

    private static string TargetLabel(string? type) => type switch
    {
        "Administrator" => "管理员账号",
        "Product" => "产品",
        "LicenseBatch" => "卡密批次",
        "LicenseKey" => "卡密",
        "DeviceBinding" => "设备绑定",
        "ApplicationSetting" => "系统设置",
        _ => "其他对象"
    };

    private static string ActorLabel(string code, AuditActorType? type, string? id)
    {
        if (type == AuditActorType.Administrator && code is "ADMIN.LOGIN_FAILED" or "ADMIN.LOGIN_LOCKED")
        {
            return Guid.TryParse(id, out _) ? "关联登录账号（未验证身份）" : "未识别的登录账号";
        }

        return type switch
        {
            AuditActorType.System => "系统",
            AuditActorType.Administrator => "管理员",
            AuditActorType.LicenseClient => "授权客户端",
            _ => "未注明主体"
        };
    }

    private static AuditActorType? ParseActorType(string value) => value switch
    {
        "System" => AuditActorType.System,
        "Administrator" => AuditActorType.Administrator,
        "LicenseClient" => AuditActorType.LicenseClient,
        _ => null
    };

    private static bool HasUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : Limit(text);
    }

    private static long? ReadPositiveInteger(JsonElement root, string name, long maximum = long.MaxValue)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number > 0 && number <= maximum ? number : null;
    }

    private static bool? ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadTerm(JsonElement root, string kindName, string unitsName, bool allowPermanent)
    {
        var kind = root.TryGetProperty(kindName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        if (kind == "Permanent" && allowPermanent)
        {
            return "永久";
        }

        var units = ReadPositiveInteger(root, unitsName, int.MaxValue);
        if (!units.HasValue)
        {
            return null;
        }

        var text = units.Value.ToString(CultureInfo.InvariantCulture);
        return kind switch
        {
            "Day" => $"{text} 天",
            "Month" => $"{text} 个月（每月 30 天）",
            "Year" => $"{text} 年（每年 365 天）",
            _ => null
        };
    }

    private static void Add(List<AuditPresentationDetail> details, string label, string value)
    {
        if (details.Count < MaximumDetailCount)
        {
            details.Add(new AuditPresentationDetail(label, Limit(value)));
        }
    }

    private static void AddText(List<AuditPresentationDetail> details, JsonElement root, string name, string label)
    {
        var text = ReadText(root, name);
        if (text is not null)
        {
            Add(details, label, text);
        }
    }

    private static void AddGuid(List<AuditPresentationDetail> details, JsonElement root, string name, string label)
    {
        if (Guid.TryParse(ReadText(root, name), out var id))
        {
            Add(details, label, id.ToString("D"));
        }
    }

    private static void AddVersion(List<AuditPresentationDetail> details, JsonElement root, string label)
    {
        var version = ReadPositiveInteger(root, "bindingVersion");
        if (version.HasValue)
        {
            Add(details, label, version.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddDate(List<AuditPresentationDetail> details, JsonElement root, string name, string label)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return;
        }
        if (value.ValueKind == JsonValueKind.Null)
        {
            Add(details, label, "未记录或尚未确定（不表示永久）");
            return;
        }

        var text = ReadText(root, name);
        var hasOffset = text is not null && (text.EndsWith('Z')
            || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
        if (hasOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Add(details, label, date.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        }
    }

    private static string Limit(string value, int length = MaximumTextLength)
    {
        var text = new string(value.Where(character => !char.IsControl(character)
            && char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray()).Trim();
        return text.Length > length ? text[..(length - 1)] + "…" : text;
    }
}
