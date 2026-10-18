using LicensePlatform.Web.CommandLine;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class WebCommandLineTests
{
    [Fact]
    public void Run_preserves_host_arguments_in_order()
    {
        string[] arguments = ["--urls", "http://127.0.0.1:5080", "--environment=Testing", "--custom", "value"];

        var result = WebCommandLine.Parse(arguments, isInputRedirected: true);

        Assert.True(result.Succeeded);
        Assert.Null(result.Error);
        Assert.Equal(WebCommandKind.Run, result.Command!.Kind);
        Assert.Null(result.Command.UserName);
        Assert.Null(result.Command.PasswordFile);
        Assert.Equal(arguments, result.Command.RemainingArguments);
        Assert.NotSame(arguments, result.Command.RemainingArguments);
    }

    [Fact]
    public void Empty_arguments_run_the_server()
    {
        var result = WebCommandLine.Parse([], isInputRedirected: true);

        Assert.True(result.Succeeded);
        Assert.Equal(WebCommandKind.Run, result.Command!.Kind);
        Assert.Empty(result.Command.RemainingArguments);
    }

    [Fact]
    public void Migrate_is_a_one_shot_command_without_administrator_values()
    {
        var result = WebCommandLine.Parse(["--migrate"], isInputRedirected: true);

        Assert.True(result.Succeeded);
        Assert.Equal(WebCommandKind.Migrate, result.Command!.Kind);
        Assert.Null(result.Command.UserName);
        Assert.Null(result.Command.PasswordFile);
        Assert.Empty(result.Command.RemainingArguments);
    }

    [Theory]
    [InlineData("--admin-init", WebCommandKind.AdminInit)]
    [InlineData("--admin-reset", WebCommandKind.AdminReset)]
    public void Administrator_commands_accept_separate_or_equals_values_and_trim_them(
        string option,
        WebCommandKind expectedKind)
    {
        var separate = WebCommandLine.Parse(
            [option, "--username", "  operator  ", "--password-file", "  C:/private/password.txt  "],
            isInputRedirected: true);
        var equals = WebCommandLine.Parse(
            ["--password-file=  C:/private/password.txt  ", "--username=  operator  ", option],
            isInputRedirected: true);

        foreach (var result in new[] { separate, equals })
        {
            Assert.True(result.Succeeded);
            Assert.Equal(expectedKind, result.Command!.Kind);
            Assert.Equal("operator", result.Command.UserName);
            Assert.Equal("C:/private/password.txt", result.Command.PasswordFile);
            Assert.Empty(result.Command.RemainingArguments);
        }
    }

    [Theory]
    [InlineData("--admin-init")]
    [InlineData("--admin-reset")]
    public void Interactive_password_requires_non_redirected_input(string option)
    {
        var interactive = WebCommandLine.Parse([option, "--username=operator"], isInputRedirected: false);
        var redirected = WebCommandLine.Parse([option, "--username=operator"], isInputRedirected: true);

        Assert.True(interactive.Succeeded);
        Assert.Null(interactive.Command!.PasswordFile);
        AssertFailure(redirected, "Administrator commands require --password-file when standard input is redirected.");
    }

    public static TheoryData<string[], string> InvalidArguments => new()
    {
        { ["--migrate", "--migrate"], "Only one command may be specified." },
        { ["--admin-init", "--admin-reset"], "Only one command may be specified." },
        { ["--migrate", "--admin-init"], "Only one command may be specified." },
        { ["--username"], "--username requires a value." },
        { ["--username", "--password-file=x"], "--username requires a value." },
        { ["--password-file"], "--password-file requires a value." },
        { ["--password-file", "--admin-init"], "--password-file requires a value." },
        { ["--username=operator"], "--username and --password-file may only be used with an administrator command." },
        { ["--password-file=private-file"], "--username and --password-file may only be used with an administrator command." },
        { ["--migrate", "--username=operator"], "--migrate does not accept --username or --password-file." },
        { ["--migrate", "--password-file=private-file"], "--migrate does not accept --username or --password-file." },
        { ["--migrate", "--urls=http://127.0.0.1:5080"], "ASP.NET Core options cannot be combined with a one-shot command." },
        { ["--admin-init", "--username=operator", "--password=synthetic-secret"], "ASP.NET Core options cannot be combined with a one-shot command." },
        { ["--admin-reset", "--username=operator", "--environment", "Testing"], "ASP.NET Core options cannot be combined with a one-shot command." },
        { ["--admin-init", "--password-file=file"], "Administrator commands require --username." },
        { ["--admin-reset", "--username=", "--password-file=file"], "Administrator commands require --username." },
        { ["--admin-init", "--username", "  ", "--password-file=file"], "Administrator commands require --username." },
        { ["--admin-init", "--username=operator", "--password-file="], "--password-file cannot be empty." },
        { ["--admin-reset", "--username=operator", "--password-file", "  "], "--password-file cannot be empty." }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Invalid_arguments_return_a_value_free_diagnostic(string[] arguments, string expectedError)
    {
        AssertFailure(WebCommandLine.Parse(arguments, isInputRedirected: true), expectedError);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => WebCommandLine.Parse(null!, isInputRedirected: false));
    }

    private static void AssertFailure(WebCommandParseResult result, string expectedError)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Command);
        Assert.Equal(expectedError, result.Error);
    }
}
