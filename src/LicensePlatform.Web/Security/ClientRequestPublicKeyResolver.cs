using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Security;

public interface IClientRequestPublicKeyResolver
{
    Task<string?> ResolveAsync(object request, CancellationToken cancellationToken);
}

public sealed class ClientRequestPublicKeyResolver(
    LicensePlatformDbContext dbContext,
    ILookupHashService lookupHashService) : IClientRequestPublicKeyResolver
{
    public async Task<string?> ResolveAsync(object request, CancellationToken cancellationToken)
    {
        switch (request)
        {
            case CreateActivationChallengeRequest activation:
                return activation.DevicePublicKey;
            case CreateLeaseRefreshChallengeRequest refresh:
                return refresh.DevicePublicKey;
            case CompleteActivationChallengeRequest complete:
                {
                    if (string.IsNullOrWhiteSpace(complete.ChallengeToken))
                    {
                        return null;
                    }

                    var challengeHash = lookupHashService.ComputeChallengeHash(complete.ChallengeToken);
                    try
                    {
                        return await dbContext.ActivationChallenges
                            .AsNoTracking()
                            .Where(challenge => challenge.ChallengeHash.SequenceEqual(challengeHash))
                            .Select(challenge => challenge.DevicePublicKey)
                            .SingleOrDefaultAsync(cancellationToken);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(challengeHash);
                    }
                }
            case ValidateLicenseRequest validation:
                if (validation.LicenseKeyId == Guid.Empty)
                {
                    return null;
                }

                return await dbContext.DeviceBindings
                    .AsNoTracking()
                    .Where(binding => binding.LicenseKeyId == validation.LicenseKeyId
                                      && binding.UnboundAtUtc == null)
                    .Select(binding => binding.DevicePublicKey)
                    .SingleOrDefaultAsync(cancellationToken);
            default:
                return null;
        }
    }
}
