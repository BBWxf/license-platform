namespace LicensePlatform.Web.CommandLine;

public enum WebCommandKind
{
    Run,
    Migrate,
    AdminInit,
    AdminReset
}

public sealed record WebCommand(
    WebCommandKind Kind,
    string? UserName,
    string? PasswordFile,
    string[] RemainingArguments);

public sealed record WebCommandParseResult(bool Succeeded, WebCommand? Command, string? Error)
{
    public static WebCommandParseResult Success(WebCommand command) => new(true, command, null);

    public static WebCommandParseResult Failure(string error) => new(false, null, error);
}

public static class WebCommandLine
{
    public const string Usage =
        "Usage:\n" +
        "  LicensePlatform.Web [ASP.NET Core options]\n" +
        "  LicensePlatform.Web --migrate\n" +
        "  LicensePlatform.Web --admin-init --username <name> --password-file <path>\n" +
        "  LicensePlatform.Web --admin-reset --username <name> --password-file <path>\n" +
        "If --password-file is omitted for an administrator command, the password is read interactively.";

    public static WebCommandParseResult Parse(string[] arguments) => Parse(arguments, Console.IsInputRedirected);

    public static WebCommandParseResult Parse(string[] arguments, bool isInputRedirected)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var commandKind = WebCommandKind.Run;
        var commandCount = 0;
        string? userName = null;
        string? passwordFile = null;
        var remaining = new List<string>();

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--migrate":
                    commandKind = WebCommandKind.Migrate;
                    commandCount++;
                    break;
                case "--admin-init":
                    commandKind = WebCommandKind.AdminInit;
                    commandCount++;
                    break;
                case "--admin-reset":
                    commandKind = WebCommandKind.AdminReset;
                    commandCount++;
                    break;
                case "--username":
                    if (!TryReadValue(arguments, ref index, out userName))
                    {
                        return WebCommandParseResult.Failure("--username requires a value.");
                    }

                    break;
                case "--password-file":
                    if (!TryReadValue(arguments, ref index, out passwordFile))
                    {
                        return WebCommandParseResult.Failure("--password-file requires a value.");
                    }

                    break;
                default:
                    if (argument.StartsWith("--username=", StringComparison.Ordinal))
                    {
                        userName = argument["--username=".Length..];
                    }
                    else if (argument.StartsWith("--password-file=", StringComparison.Ordinal))
                    {
                        passwordFile = argument["--password-file=".Length..];
                    }
                    else
                    {
                        remaining.Add(argument);
                    }

                    break;
            }
        }

        if (commandCount > 1)
        {
            return WebCommandParseResult.Failure("Only one command may be specified.");
        }

        if (commandKind == WebCommandKind.Run)
        {
            if (userName is not null || passwordFile is not null)
            {
                return WebCommandParseResult.Failure(
                    "--username and --password-file may only be used with an administrator command.");
            }

            return WebCommandParseResult.Success(new WebCommand(commandKind, null, null, [.. remaining]));
        }

        if (remaining.Count > 0)
        {
            return WebCommandParseResult.Failure("ASP.NET Core options cannot be combined with a one-shot command.");
        }

        if (commandKind == WebCommandKind.Migrate)
        {
            if (userName is not null || passwordFile is not null)
            {
                return WebCommandParseResult.Failure(
                    "--migrate does not accept --username or --password-file.");
            }

            return WebCommandParseResult.Success(new WebCommand(commandKind, null, null, []));
        }

        userName = userName?.Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return WebCommandParseResult.Failure("Administrator commands require --username.");
        }

        if (passwordFile is not null)
        {
            passwordFile = passwordFile.Trim();
            if (passwordFile.Length == 0)
            {
                return WebCommandParseResult.Failure("--password-file cannot be empty.");
            }
        }
        else if (isInputRedirected)
        {
            return WebCommandParseResult.Failure(
                "Administrator commands require --password-file when standard input is redirected.");
        }

        return WebCommandParseResult.Success(new WebCommand(commandKind, userName, passwordFile, []));
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        out string? value)
    {
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
