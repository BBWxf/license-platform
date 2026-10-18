using System;
using System.Net.Http;

namespace Licensing.Client.Windows
{
    public static class WindowsLicenseClientFactory
    {
        /// <summary>
        /// Creates a client with the Windows 2-of-3 hardware fingerprint and current-user DPAPI stores.
        /// The caller owns the supplied handler. Built-in HttpClientHandler redirects are forcibly disabled.
        /// </summary>
        public static LicenseClient Create(HttpMessageHandler httpHandler, LicenseClientOptions clientOptions, WindowsStorageOptions storageOptions)
        {
            if (httpHandler == null) throw new ArgumentNullException(nameof(httpHandler));
            if (clientOptions == null) throw new ArgumentNullException(nameof(clientOptions));
            if (storageOptions == null) throw new ArgumentNullException(nameof(storageOptions));
            return new LicenseClient(
                httpHandler,
                clientOptions,
                new WindowsMachineFingerprintProvider(),
                new DpapiInstallationKeyStore(storageOptions),
                new DpapiLicenseStateStore(storageOptions));
        }
    }
}
