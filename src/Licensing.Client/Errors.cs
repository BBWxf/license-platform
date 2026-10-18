using System;
using System.Net;

namespace Licensing.Client
{
    public enum LicenseErrorCode
    {
        None = 0,
        InvalidConfiguration,
        StorageFailure,
        FingerprintUnavailable,
        KeyGenerationFailed,
        NetworkUnavailable,
        ServerError,
        BusinessRejected,
        InvalidResponse,
        UnsupportedAlgorithm,
        UnknownSigningKey,
        InvalidServerSignature,
        LeaseMalformed,
        LeaseExpired,
        LeaseNotYetValid,
        LeaseLifetimeExceeded,
        LeaseBindingMismatch,
        TrustedTimeRollback,
        ActivationRequired,
        OperationCancelled,
        ClientFailure
    }

    public sealed class LicenseError
    {
        public LicenseError(
            LicenseErrorCode code,
            string message,
            bool isTransient = false,
            HttpStatusCode? httpStatusCode = null,
            string? serverCode = null)
        {
            Code = code;
            Message = message;
            IsTransient = isTransient;
            HttpStatusCode = httpStatusCode;
            ServerCode = serverCode;
        }

        public LicenseErrorCode Code { get; }
        public string Message { get; }
        public bool IsTransient { get; }
        public HttpStatusCode? HttpStatusCode { get; }
        public string? ServerCode { get; }

        public override string ToString() => ServerCode == null
            ? Code + ": " + Message
            : Code + "/" + ServerCode + ": " + Message;
    }

    public sealed class LicenseResult
    {
        private LicenseResult(bool success, LicenseLease? lease, LicenseError? error, bool usedOfflineFallback)
        {
            Success = success;
            Lease = lease;
            Error = error;
            UsedOfflineFallback = usedOfflineFallback;
        }

        public bool Success { get; }
        public LicenseLease? Lease { get; }
        public LicenseError? Error { get; }
        public bool UsedOfflineFallback { get; }

        public static LicenseResult Succeeded(LicenseLease lease, bool usedOfflineFallback = false) =>
            new LicenseResult(true, lease ?? throw new ArgumentNullException(nameof(lease)), null, usedOfflineFallback);

        public static LicenseResult Failed(LicenseError error) =>
            new LicenseResult(false, null, error ?? throw new ArgumentNullException(nameof(error)), false);
    }

    public sealed class LicensingException : Exception
    {
        public LicensingException(
            LicenseErrorCode code,
            string message,
            Exception? innerException = null,
            bool isTransient = false,
            HttpStatusCode? httpStatusCode = null,
            string? serverCode = null)
            : base(message, innerException)
        {
            Code = code;
            IsTransient = isTransient;
            HttpStatusCode = httpStatusCode;
            ServerCode = serverCode;
        }

        public LicenseErrorCode Code { get; }
        public bool IsTransient { get; }
        public HttpStatusCode? HttpStatusCode { get; }
        public string? ServerCode { get; }

        internal LicenseError ToError() => new LicenseError(Code, Message, IsTransient, HttpStatusCode, ServerCode);
    }
}
