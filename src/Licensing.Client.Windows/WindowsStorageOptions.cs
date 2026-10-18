using System;
using System.IO;
using System.Linq;

namespace Licensing.Client.Windows
{
    public sealed class WindowsStorageOptions
    {
        public WindowsStorageOptions(string applicationName)
        {
            if (string.IsNullOrWhiteSpace(applicationName))
                throw new ArgumentException("A stable application name is required.", nameof(applicationName));
            if (applicationName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || applicationName == "." || applicationName == "..")
                throw new ArgumentException("Application name contains invalid path characters.", nameof(applicationName));
            ApplicationName = applicationName;
        }

        public string ApplicationName { get; }

        /// <summary>Defaults to %LOCALAPPDATA%\LicensingSdk\{ApplicationName}.</summary>
        public string? DirectoryPath { get; set; }

        internal string ResolveDirectory()
        {
            var root = DirectoryPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    throw new InvalidOperationException("LOCALAPPDATA is unavailable.");
                root = Path.Combine(local, "LicensingSdk", ApplicationName);
            }
            return Path.GetFullPath(root!);
        }
    }
}
