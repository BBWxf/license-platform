namespace LicensePlatform.Application.Abstractions;

public interface IDeviceProofVerifier
{
    bool VerifyEs256(string devicePublicKeyPem, ReadOnlySpan<byte> challengeNonce, string proofSignature);
}
