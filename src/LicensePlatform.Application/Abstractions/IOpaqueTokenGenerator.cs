namespace LicensePlatform.Application.Abstractions;

public interface IOpaqueTokenGenerator
{
    string Generate(int entropyBytes = 32);
}
