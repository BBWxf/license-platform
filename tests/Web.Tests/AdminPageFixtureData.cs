using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Http;

namespace LicensePlatform.Web.Tests;

public enum AdminPageDataState
{
    Empty,
    Populated,
    LongText
}

// Read-only deterministic presentation data, not a database/service integration substitute.
// No complete license keys, raw hardware IDs, passwords, signing keys, or real user data.
internal sealed class AdminPageFixtureData : IAdminDashboardQuery, IProductAdminService,
    ILicenseBatchAdminService, ILicenseAdminService, IAuditAdminService, ISystemSettingsService,
    IAdminAuthenticationService
{
    internal static readonly DateTimeOffset Timestamp = new(2026, 10, 18, 8, 30, 0, TimeSpan.Zero);
    internal static readonly Guid ProductId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    internal static readonly Guid BatchId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    internal static readonly Guid LicenseId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    internal static readonly Guid BindingId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    internal const string EncodingProbe = "Fixture-<render-probe>&";
    internal const string LicensePublicId = "lic_fixture_public_01";
    internal const string BindingPublicId = "bind_fixture_public_01";
    private readonly AdminPageDataState _state;
    private int _mutationCalls;
    private int _authenticationCalls;

    internal AdminPageFixtureData(AdminPageDataState state)
    {
        _state = state;
        ProductName = state == AdminPageDataState.LongText ? EncodingProbe + new string('W', 96) : "Fixture Desktop Product";
        BatchName = state == AdminPageDataState.LongText ? EncodingProbe + new string('B', 96) : "Fixture October Batch";
        Summary = state == AdminPageDataState.LongText ? EncodingProbe + new string('S', 440) : "Synthetic redacted administration event";
        SystemName = state == AdminPageDataState.LongText ? "Regression Fixture " + new string('W', 58) : "Regression Fixture";
    }

    internal string ProductName { get; }
    internal string BatchName { get; }
    internal string Summary { get; }
    internal string SystemName { get; }
    internal bool IsEmpty => _state == AdminPageDataState.Empty;
    internal int MutationCalls => Volatile.Read(ref _mutationCalls);
    internal int AuthenticationCalls => Volatile.Read(ref _authenticationCalls);
    internal IReadOnlyList<ProductOption> Options => IsEmpty ? [] : [new(ProductId, ProductName)];

    Task<AdminDashboardSnapshot> IAdminDashboardQuery.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsEmpty
            ? new AdminDashboardSnapshot(0, 0, 0, 0, 0, [])
            : new AdminDashboardSnapshot(7, 1234, 567, 89, 321,
                [new(Timestamp, "FIXTURE.PRODUCT.UPDATE", "Product", "SUCCESS", Summary)]));

    Task<IReadOnlyList<ProductListItem>> IProductAdminService.ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProductListItem>>(IsEmpty ? [] :
        [
            new(ProductId, ProductName, "product_fixture_public_01", "DEMO", true, 1234, 321, Timestamp),
            new(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Fixture Disabled Product", "product_fixture_public_02", "TEST", false, 4, 0, Timestamp)
        ]);

    Task<ProductEditor?> IProductAdminService.GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<ProductEditor?>(id == ProductId ? new() { Name = ProductName, LicensePrefix = "DEMO" } : null);

    Task<Guid> IProductAdminService.SaveAsync(Guid? id, ProductEditor input, Guid actorId, string sourceIp, CancellationToken cancellationToken) =>
        ReadOnly<Guid>();

    Task IProductAdminService.SetEnabledAsync(Guid id, bool enabled, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();

    Task<ClientConfigDownload?> IProductAdminService.GetClientConfigAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<ClientConfigDownload?>(null);

    Task<IReadOnlyList<BatchListItem>> ILicenseBatchAdminService.ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BatchListItem>>(IsEmpty ? [] :
        [
            new(BatchId, BatchName, ProductName, 250, 250, AdminValidityKind.Month, 3, "ISSUED", true, Timestamp),
            new(Guid.Parse("30000000-0000-0000-0000-000000000002"), "Fixture Permanent Batch", ProductName, 50, 50,
                AdminValidityKind.Permanent, 0, "COMPLETED", false, Timestamp)
        ]);

    Task<IReadOnlyList<ProductOption>> ILicenseBatchAdminService.GetProductOptionsAsync(CancellationToken cancellationToken) => Task.FromResult(Options);

    Task<Guid> ILicenseBatchAdminService.CreateAsync(CreateBatchCommand command, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly<Guid>();

    Task<BatchExportDownload?> ILicenseBatchAdminService.DownloadExportAsync(Guid batchId, Guid actorId, string sourceIp, CancellationToken cancellationToken) =>
        Task.FromResult<BatchExportDownload?>(null);

    Task ILicenseBatchAdminService.DisableAllAsync(Guid batchId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();

    Task<LicenseSearchResult> ILicenseAdminService.SearchAsync(LicenseSearchQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new LicenseSearchResult(IsEmpty ? [] :
        [
            new(LicenseId, LicensePublicId, ProductName, "4F2A", "ACTIVE", true, Timestamp.AddDays(-2), Timestamp.AddDays(88), 3),
            new(Guid.Parse("40000000-0000-0000-0000-000000000002"), "lic_fixture_public_02", ProductName, "8C1B", "UNUSED", true, null, null, 1),
            new(Guid.Parse("40000000-0000-0000-0000-000000000003"), "lic_fixture_public_03", ProductName, "3D9E", "EXPIRED", true, Timestamp.AddDays(-40), Timestamp.AddDays(-10), 2),
            new(Guid.Parse("40000000-0000-0000-0000-000000000004"), "lic_fixture_public_04", ProductName, "5A7F", "DISABLED", false, Timestamp.AddDays(-3), null, 5)
        ], query.Page, query.PageSize, IsEmpty ? 0 : 104, Options));

    Task<LicenseDetails?> ILicenseAdminService.GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<LicenseDetails?>(id != LicenseId ? null : new LicenseDetails(
            LicenseId, LicensePublicId, ProductName, BatchName, "4F2A", IsEmpty ? "UNUSED" : "ACTIVE", true,
            IsEmpty ? AdminValidityKind.Permanent : AdminValidityKind.Month, IsEmpty ? 0 : 3,
            IsEmpty ? null : Timestamp.AddDays(-2), IsEmpty ? null : Timestamp.AddDays(88), 3,
            _state == AdminPageDataState.LongText ? Summary : null, Timestamp,
            IsEmpty ? [] :
            [
                new(BindingId, BindingPublicId, "ACTIVE", "sha256:masked…4f2a", Timestamp.AddDays(-2), Timestamp, "1.2.3-fixture", null, null),
                new(Guid.Parse("50000000-0000-0000-0000-000000000002"), "bind_fixture_public_02", "UNBOUND", "sha256:masked…8c1b",
                    Timestamp.AddDays(-5), Timestamp.AddDays(-3), null, Timestamp.AddDays(-2), Summary)
            ]));

    Task ILicenseAdminService.SetEnabledAsync(Guid id, bool enabled, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();
    Task ILicenseAdminService.ExtendAsync(Guid id, AdminValidityKind kind, int units, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();
    Task ILicenseAdminService.UnbindAsync(Guid bindingId, string reason, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();

    Task<AuditSearchResult> IAuditAdminService.SearchAsync(int page, int pageSize, string? action, CancellationToken cancellationToken) =>
        Task.FromResult(new AuditSearchResult(IsEmpty ? [] :
        [
            new(Timestamp, "fixture-operator", "FIXTURE.PRODUCT.UPDATE", "Product", "product_fixture_public_01", "SUCCESS", "127.0.0.1", Summary),
            new(Timestamp.AddMinutes(-1), "fixture-operator", "FIXTURE.LOGIN", "Administrator", null, "FAILURE", "127.0.0.1", null)
        ], page, pageSize, IsEmpty ? 0 : 78));

    Task<SystemSettingsEditor> ISystemSettingsService.GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SystemSettingsEditor(SystemName, !IsEmpty, "instance-fixture-local", "test-fixture-version"));

    Task<BrandLogo?> ISystemSettingsService.GetLogoAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BrandLogo?>(IsEmpty ? null : new BrandLogo(
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="), "image/png"));

    Task ISystemSettingsService.UpdateAsync(string systemName, IFormFile? logo, Guid actorId, string sourceIp, CancellationToken cancellationToken) => ReadOnly();

    Task<AdminAuthenticationResult> IAdminAuthenticationService.AuthenticateAsync(string userName, string password, string sourceIp, CancellationToken cancellationToken)
    {
        // Never retain or log submitted credentials. The fixture deliberately cannot password-login.
        Interlocked.Increment(ref _authenticationCalls);
        return Task.FromResult(new AdminAuthenticationResult(false));
    }

    private Task ReadOnly()
    {
        Interlocked.Increment(ref _mutationCalls);
        throw new InvalidOperationException("Page rendering fixtures are read-only.");
    }

    private Task<T> ReadOnly<T>()
    {
        Interlocked.Increment(ref _mutationCalls);
        throw new InvalidOperationException("Page rendering fixtures are read-only.");
    }
}
