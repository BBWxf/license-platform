using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Licensing.Client.Windows
{
    /// <summary>
    /// Builds windows-hardware-v1 from SHA-256 hashes of MachineGuid, SMBIOS UUID, and system-volume serial.
    /// Raw identifiers never leave this provider. At least two components are required and leases match when two components agree.
    /// </summary>
    public sealed class WindowsMachineFingerprintProvider : IMachineFingerprintProvider
    {
        public const string FingerprintVersion = "windows-hardware-v1";
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private MachineFingerprint? _cached;

        public async Task<MachineFingerprint> GetAsync(CancellationToken cancellationToken)
        {
            if (_cached != null)
                return _cached;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cached != null)
                    return _cached;
                var created = await Task.Run(() => Create(cancellationToken), cancellationToken).ConfigureAwait(false);
                _cached = created;
                return created;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static MachineFingerprint Create(CancellationToken cancellationToken)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                throw new PlatformNotSupportedException("The Windows machine fingerprint provider requires Windows.");

            var components = new Dictionary<string, string>(StringComparer.Ordinal);
            Add(components, "machineGuid", TryGetMachineGuid());
            cancellationToken.ThrowIfCancellationRequested();
            Add(components, "smbiosUuid", TryGetSmbiosUuid());
            cancellationToken.ThrowIfCancellationRequested();
            Add(components, "systemVolume", TryGetSystemVolumeSerial());

            if (components.Count < 2)
                throw new InvalidOperationException("At least two of MachineGuid, SMBIOS UUID, and system-volume serial are required.");

            var canonical = FingerprintVersion + "\n" + string.Join("\n", components.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            return new MachineFingerprint(FingerprintVersion, Sha256Hex(canonical), components, requiredMatches: 2);
        }

        private static void Add(IDictionary<string, string> destination, string name, string? rawValue)
        {
            if (!string.IsNullOrWhiteSpace(rawValue))
                destination[name] = Sha256Hex(FingerprintVersion + "\n" + name + "\n" + rawValue);
        }

        private static string? TryGetMachineGuid()
        {
            try
            {
                using (var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false))
                {
                    var value = key?.GetValue("MachineGuid") as string;
                    return NormalizeIdentifier(value);
                }
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetSmbiosUuid()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT UUID FROM Win32_ComputerSystemProduct"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            var value = Convert.ToString(item["UUID"], CultureInfo.InvariantCulture);
                            if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty &&
                                !string.Equals(parsed.ToString("N"), "ffffffffffffffffffffffffffffffff", StringComparison.OrdinalIgnoreCase))
                                return parsed.ToString("N").ToUpperInvariant();
                        }
                    }
                }
            }
            catch
            {
                // WMI can be disabled; the 2-of-3 model tolerates one unavailable source.
            }
            return null;
        }

        private static string? TryGetSystemVolumeSerial()
        {
            try
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var root = Path.GetPathRoot(windows);
                if (string.IsNullOrWhiteSpace(root))
                    return null;

                var volumeName = new StringBuilder(261);
                var fileSystemName = new StringBuilder(261);
                if (!GetVolumeInformation(root, volumeName, volumeName.Capacity, out var serial, out _, out _, fileSystemName, fileSystemName.Capacity) || serial == 0)
                    return null;
                return serial.ToString("X8", CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string? NormalizeIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (Guid.TryParse(value, out var parsed))
            {
                var guid = parsed.ToString("N");
                if (parsed == Guid.Empty || string.Equals(guid, "ffffffffffffffffffffffffffffffff", StringComparison.OrdinalIgnoreCase))
                    return null;
                return guid.ToUpperInvariant();
            }
            var normalized = new string(value!.Trim().Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
            if (normalized.Length == 0 || normalized.All(character => character == '0') || normalized.All(character => character == 'F'))
                return null;
            return normalized;
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes)
                    builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);
    }
}
