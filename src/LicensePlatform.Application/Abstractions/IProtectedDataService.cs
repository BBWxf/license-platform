namespace LicensePlatform.Application.Abstractions;

public interface IProtectedDataService
{
    byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData, string purpose);
}
