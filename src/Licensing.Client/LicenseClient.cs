using System;
using System.Threading;
using System.Threading.Tasks;

namespace Licensing.Client
{
    /// <summary>
    /// Coordinates activation, online validation, challenge-based lease refresh, signed offline leases, and rollback detection.
    /// Call <see cref="StartAsync(CancellationToken)"/> once during every process startup; unlike <see cref="GetLeaseAsync(CancellationToken)"/>, it always contacts the service first.
    /// </summary>
    public sealed class LicenseClient : IDisposable
    {
        private readonly System.Net.Http.HttpClient _httpClient;
        private readonly LicenseClientOptions _options;
        private readonly IMachineFingerprintProvider _fingerprintProvider;
        private readonly ILicenseStateStore _stateStore;
        private readonly ILicenseClock _clock;
        private readonly InstallationKeyManager _keyManager;
        private readonly LicenseApiClient _api;
        private readonly JwsLeaseValidator _validator;
        private readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Creates a client over a caller-owned handler. Automatic redirects are forcibly disabled when the
        /// handler chain uses <see cref="System.Net.Http.HttpClientHandler"/> so activation credentials and signed
        /// requests cannot be forwarded before the SDK can validate the response origin.
        /// </summary>
        public LicenseClient(
            System.Net.Http.HttpMessageHandler httpHandler,
            LicenseClientOptions options,
            IMachineFingerprintProvider fingerprintProvider,
            IInstallationKeyStore installationKeyStore,
            ILicenseStateStore stateStore,
            ILicenseClock? clock = null)
        {
            if (httpHandler == null) throw new ArgumentNullException(nameof(httpHandler));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            DisableAutomaticRedirects(httpHandler);
            _httpClient = new System.Net.Http.HttpClient(httpHandler, disposeHandler: false)
            {
                Timeout = _options.RequestTimeout
            };
            _fingerprintProvider = fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _clock = clock ?? SystemLicenseClock.Instance;
            _keyManager = new InstallationKeyManager(installationKeyStore ?? throw new ArgumentNullException(nameof(installationKeyStore)), _clock);
            _api = new LicenseApiClient(_httpClient, _options, _clock);
            _validator = new JwsLeaseValidator(_options);
        }

        /// <summary>
        /// Startup entry point for an already activated installation. The license key is not required or persisted;
        /// online validation is attempted first and a cached lease is used only for network failures or HTTP 5xx.
        /// </summary>
        public Task<LicenseResult> StartAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(licenseKey: null, forceOnline: true, cancellationToken);

        /// <summary>
        /// Startup/activation entry point when the operator supplied a license key. The key is used only for
        /// first activation and cache selection, and is never written to the SDK state store.
        /// </summary>
        public Task<LicenseResult> StartAsync(string licenseKey, CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(licenseKey, forceOnline: true, cancellationToken);

        /// <summary>
        /// Runtime entry point for an already activated installation. Uses a valid cache until the two-hour
        /// refresh interval is reached, then validates and refreshes online without resubmitting the license key.
        /// </summary>
        public Task<LicenseResult> GetLeaseAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(licenseKey: null, forceOnline: false, cancellationToken);

        /// <summary>
        /// Runtime entry point that additionally verifies the supplied license key selects the current cache.
        /// </summary>
        public Task<LicenseResult> GetLeaseAsync(string licenseKey, CancellationToken cancellationToken = default(CancellationToken)) =>
            ExecuteAsync(licenseKey, forceOnline: false, cancellationToken);

        public async Task<LicenseResult> ClearAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ClearStateAsync(cancellationToken).ConfigureAwait(false);
                    return LicenseResult.Failed(new LicenseError(LicenseErrorCode.ActivationRequired, "The cached lease was cleared; online activation is required."));
                }
                finally
                {
                    _operationGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return LicenseResult.Failed(new LicenseError(LicenseErrorCode.OperationCancelled, "The licensing operation was cancelled."));
            }
            catch (LicensingException ex)
            {
                return LicenseResult.Failed(ex.ToError());
            }
        }

        private async Task<LicenseResult> ExecuteAsync(string? licenseKey, bool forceOnline, CancellationToken cancellationToken)
        {
            if (licenseKey != null && string.IsNullOrWhiteSpace(licenseKey))
                return LicenseResult.Failed(new LicenseError(LicenseErrorCode.InvalidConfiguration, "A supplied license key cannot be empty."));

            try
            {
                await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ExecuteLockedAsync(licenseKey, forceOnline, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _operationGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return LicenseResult.Failed(new LicenseError(LicenseErrorCode.OperationCancelled, "The licensing operation was cancelled."));
            }
            catch (LicensingException ex)
            {
                Log("operation-failed", ex.Code, ex.Message);
                return LicenseResult.Failed(ex.ToError());
            }
            catch (Exception ex)
            {
                Log("operation-failed", LicenseErrorCode.ClientFailure, ex.Message);
                return LicenseResult.Failed(new LicenseError(LicenseErrorCode.ClientFailure, "The licensing client encountered an unexpected failure."));
            }
        }

        private async Task<LicenseResult> ExecuteLockedAsync(string? licenseKey, bool forceOnline, CancellationToken cancellationToken)
        {
            var installation = await _keyManager.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var fingerprint = await GetFingerprintAsync(cancellationToken).ConfigureAwait(false);
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var suppliedLicenseKeyHash = licenseKey == null ? null : HashLicenseKey(licenseKey);

            if (state != null && suppliedLicenseKeyHash != null &&
                !MachineFingerprint.FixedTimeEquals(state.LicenseKeyHash ?? string.Empty, suppliedLicenseKeyHash))
            {
                await ClearStateAsync(cancellationToken).ConfigureAwait(false);
                state = null;
                Log("license-key-changed", LicenseErrorCode.None, "Cached state for a different product or license key was removed.");
            }

            LicenseLease? cachedLease = null;
            LicenseLease? refreshMetadata = null;
            LicensingException? cachedError = null;
            if (state != null)
            {
                var effectiveTime = Max(_clock.UtcNow, state.TrustedUtc);
                try
                {
                    cachedLease = ValidateCached(state, installation, fingerprint);
                    refreshMetadata = cachedLease;
                }
                catch (LicensingException ex)
                {
                    cachedError = ex;
                    Log("cache-invalid", ex.Code, ex.Message);
                    try
                    {
                        // An expired but correctly signed lease may supply license id and binding version
                        // for server-authorized refresh; it is never accepted for offline use.
                        refreshMetadata = _validator.Validate(state.LeaseToken, installation, effectiveTime, allowExpiredForOnlineRefresh: true);
                    }
                    catch (LicensingException metadataError)
                    {
                        Log("refresh-metadata-invalid", metadataError.Code, metadataError.Message);
                    }
                }
            }

            if (!forceOnline && cachedLease != null && !RefreshIsDue(state!, cachedLease))
            {
                await RecordLocalObservationAsync(state!, cancellationToken).ConfigureAwait(false);
                Log("cache-hit", LicenseErrorCode.None, "Using a valid signed lease before its refresh interval.");
                return LicenseResult.Succeeded(cachedLease);
            }

            try
            {
                if (state == null)
                {
                    if (licenseKey == null)
                    {
                        return LicenseResult.Failed(new LicenseError(
                            LicenseErrorCode.ActivationRequired,
                            "No cached activation is available; supply a license key once to activate this installation."));
                    }

                    var activationChallenge = await _api.CreateActivationChallengeAsync(licenseKey, installation, fingerprint, cancellationToken).ConfigureAwait(false);
                    EnsureChallengeIsCurrent(activationChallenge);
                    var activation = await _api.CompleteChallengeAsync(activationChallenge, installation, fingerprint, cancellationToken).ConfigureAwait(false);
                    var activatedLease = ValidateOnlineLease(activation, installation, state: null);
                    await SaveNewLeaseAsync(activation, activatedLease, fingerprint, suppliedLicenseKeyHash!, cancellationToken).ConfigureAwait(false);
                    Log("activated", LicenseErrorCode.None, "The activation challenge was completed and its signed lease was cached without storing the license key.");
                    return LicenseResult.Succeeded(activatedLease);
                }

                if (refreshMetadata == null)
                    return LicenseResult.Failed((cachedError ?? new LicensingException(LicenseErrorCode.LeaseMalformed, "The cached lease cannot supply authenticated refresh metadata.")).ToError());

                var licenseId = Guid.Parse(refreshMetadata.LicenseId);
                var validation = await _api.ValidateAsync(licenseId, refreshMetadata.BindingVersion, installation, fingerprint, cancellationToken).ConfigureAwait(false);
                if (!validation.Validation.IsValid)
                    throw new LicensingException(
                        LicenseErrorCode.BusinessRejected,
                        "The licensing service rejected the cached binding: " + (validation.Validation.FailureCode ?? "invalid"),
                        serverCode: validation.Validation.FailureCode);
                if (validation.Validation.CurrentBindingVersion != refreshMetadata.BindingVersion)
                    throw new LicensingException(LicenseErrorCode.InvalidResponse, "The validation response binding version does not match the signed lease.");

                var refreshDue = cachedLease == null || RefreshIsDue(state, refreshMetadata);
                if (refreshDue)
                {
                    var refreshChallenge = await _api.CreateRefreshChallengeAsync(
                        licenseId,
                        refreshMetadata.BindingVersion,
                        installation,
                        fingerprint,
                        cancellationToken).ConfigureAwait(false);
                    EnsureChallengeIsCurrent(refreshChallenge);
                    var refreshed = await _api.CompleteChallengeAsync(refreshChallenge, installation, fingerprint, cancellationToken).ConfigureAwait(false);
                    var refreshedLease = ValidateOnlineLease(refreshed, installation, state);
                    var cachedLicenseKeyHash = state.LicenseKeyHash;
                    if (string.IsNullOrWhiteSpace(cachedLicenseKeyHash))
                        throw new LicensingException(LicenseErrorCode.StorageFailure, "The cached license selector is missing.");
                    await SaveNewLeaseAsync(refreshed, refreshedLease, fingerprint, cachedLicenseKeyHash!, cancellationToken).ConfigureAwait(false);
                    Log("lease-refreshed", LicenseErrorCode.None, "The refresh challenge was completed without resubmitting the license key.");
                    return LicenseResult.Succeeded(refreshedLease);
                }

                await RecordOnlineValidationAsync(state, validation.ServerTimeUtc, cancellationToken).ConfigureAwait(false);
                Log("validated-online", LicenseErrorCode.None, "The service confirmed the cached license binding; the current signed lease remains in use.");
                return LicenseResult.Succeeded(cachedLease!);
            }
            catch (LicensingException ex)
            {
                if (ex.Code == LicenseErrorCode.BusinessRejected)
                {
                    await ClearStateAsync(cancellationToken).ConfigureAwait(false);
                    Log("business-rejected", ex.Code, "The service rejected the license or device binding; cached state was cleared.");
                    return LicenseResult.Failed(ex.ToError());
                }

                if (IsOfflineFallbackEligible(ex) && cachedLease != null && state != null)
                {
                    await RecordLocalObservationAsync(state, cancellationToken).ConfigureAwait(false);
                    Log("offline-fallback", ex.Code, "The service was unavailable; using the still-valid signed lease.");
                    return LicenseResult.Succeeded(cachedLease, usedOfflineFallback: true);
                }

                if (IsOfflineFallbackEligible(ex) && cachedError?.Code == LicenseErrorCode.TrustedTimeRollback)
                    return LicenseResult.Failed(cachedError.ToError());

                return LicenseResult.Failed(ex.ToError());
            }
        }

        private LicenseLease ValidateOnlineLease(ApiLeaseResult result, InstallationKeyRecord installation, LicenseStateRecord? state)
        {
            var localNow = _clock.UtcNow;
            var responseTime = result.ServerTimeUtc ?? localNow;
            var validationTime = Max(responseTime, state?.TrustedUtc ?? DateTimeOffset.MinValue);
            var lease = _validator.Validate(result.Token, installation, validationTime);
            if (!Guid.TryParse(lease.LicenseId, out var signedLicenseId) || signedLicenseId != result.LicenseKeyId ||
                lease.BindingVersion != result.BindingVersion)
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The activation result does not match its signed lease claims.");
            return lease;
        }

        private async Task SaveNewLeaseAsync(
            ApiLeaseResult result,
            LicenseLease lease,
            MachineFingerprint fingerprint,
            string licenseKeyHash,
            CancellationToken cancellationToken)
        {
            var localNow = _clock.UtcNow;
            var trustedTime = Max(result.ServerTimeUtc ?? localNow, lease.IssuedAtUtc);
            var newState = new LicenseStateRecord
            {
                Version = 1,
                LeaseToken = result.Token,
                LicenseKeyHash = licenseKeyHash,
                LastOnlineUtc = lease.IssuedAtUtc,
                TrustedUtc = trustedTime,
                LastObservedUtc = localNow,
                MachineFingerprint = fingerprint
            };
            await SaveStateAsync(newState, cancellationToken).ConfigureAwait(false);
        }

        private void EnsureChallengeIsCurrent(ApiChallengeResult result)
        {
            var referenceTime = result.ServerTimeUtc ?? _clock.UtcNow;
            if (result.Challenge.ExpiresAtUtc < referenceTime - _options.ClockSkew)
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The server returned an expired activation challenge.");
        }

        private LicenseLease ValidateCached(LicenseStateRecord state, InstallationKeyRecord installation, MachineFingerprint currentFingerprint)
        {
            if (state.Version != 1 || string.IsNullOrWhiteSpace(state.LeaseToken) || string.IsNullOrWhiteSpace(state.LicenseKeyHash) || state.MachineFingerprint == null)
                throw new LicensingException(LicenseErrorCode.StorageFailure, "The cached license state is malformed or unsupported.");
            if (!currentFingerprint.Matches(state.MachineFingerprint))
                throw new LicensingException(LicenseErrorCode.LeaseBindingMismatch, "Fewer than two protected machine fingerprint components match this device.");

            var now = _clock.UtcNow;
            if (state.LastObservedUtc != default(DateTimeOffset) && now + _options.ClockRollbackTolerance < state.LastObservedUtc)
                throw new LicensingException(LicenseErrorCode.TrustedTimeRollback, "The local clock moved backwards beyond the permitted tolerance; online validation is required.");

            return _validator.Validate(state.LeaseToken, installation, Max(now, state.TrustedUtc));
        }

        private bool RefreshIsDue(LicenseStateRecord state, LicenseLease lease)
        {
            var effectiveNow = Max(_clock.UtcNow, state.TrustedUtc);
            if (effectiveNow >= lease.ExpiresAtUtc - _options.ClockSkew)
                return true;
            return state.LastOnlineUtc == default(DateTimeOffset) || effectiveNow - state.LastOnlineUtc >= _options.RefreshInterval;
        }

        private async Task RecordOnlineValidationAsync(LicenseStateRecord state, DateTimeOffset? serverTimeUtc, CancellationToken cancellationToken)
        {
            var now = _clock.UtcNow;
            state.TrustedUtc = Max(state.TrustedUtc, serverTimeUtc ?? now);
            state.LastObservedUtc = now;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordLocalObservationAsync(LicenseStateRecord state, CancellationToken cancellationToken)
        {
            var now = _clock.UtcNow;
            if (now > state.LastObservedUtc)
                state.LastObservedUtc = now;
            if (state.TrustedUtc == default(DateTimeOffset))
                state.TrustedUtc = now;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
        }

        private async Task<MachineFingerprint> GetFingerprintAsync(CancellationToken cancellationToken)
        {
            try
            {
                var value = await _fingerprintProvider.GetAsync(cancellationToken).ConfigureAwait(false);
                if (value == null || string.IsNullOrWhiteSpace(value.Version) || string.IsNullOrWhiteSpace(value.Digest) ||
                    value.RequiredMatches < 1 || value.Components.Count < value.RequiredMatches)
                    throw new LicensingException(LicenseErrorCode.FingerprintUnavailable, "The machine fingerprint does not contain enough usable components.");
                return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.FingerprintUnavailable, "Could not create the machine fingerprint.", ex);
            }
        }

        private async Task<LicenseStateRecord?> LoadStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not load the cached license state.", ex);
            }
        }

        private async Task SaveStateAsync(LicenseStateRecord state, CancellationToken cancellationToken)
        {
            try
            {
                await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not persist the cached license state.", ex);
            }
        }

        private async Task ClearStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _stateStore.ClearAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.StorageFailure, "Could not clear the cached license state.", ex);
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _operationGate.Dispose();
        }

        private static void DisableAutomaticRedirects(System.Net.Http.HttpMessageHandler handler)
        {
            var current = handler;
            while (true)
            {
                if (current is System.Net.Http.HttpClientHandler clientHandler)
                {
                    try
                    {
                        clientHandler.AllowAutoRedirect = false;
                        return;
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new LicensingException(
                            LicenseErrorCode.InvalidConfiguration,
                            "The supplied HttpClientHandler was already used and its redirect policy cannot be secured. Supply a fresh handler.",
                            exception);
                    }
                }

                if (current is not System.Net.Http.DelegatingHandler delegatingHandler)
                {
                    return;
                }

                current = delegatingHandler.InnerHandler
                    ?? throw new LicensingException(
                        LicenseErrorCode.InvalidConfiguration,
                        "The supplied DelegatingHandler chain has no primary handler.");
            }
        }

        private static bool IsOfflineFallbackEligible(LicensingException exception) =>
            exception.Code == LicenseErrorCode.NetworkUnavailable || exception.Code == LicenseErrorCode.ServerError;

        private string HashLicenseKey(string licenseKey) =>
            CryptoUtilities.Sha256Base64Url("license-key-v1\n" + _options.ProductCode + "\n" + licenseKey);

        private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

        private void Log(string eventName, LicenseErrorCode code, string message)
        {
            try
            {
                _options.Logger?.Invoke(new LicenseLogEntry(_clock.UtcNow, eventName, code, message));
            }
            catch { }
        }
    }
}
