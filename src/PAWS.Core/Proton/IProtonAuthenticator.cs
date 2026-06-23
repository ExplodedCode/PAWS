namespace PAWS.Core.Proton;

/// <summary>
/// Port for Proton authentication. The real adapter (in the planned PAWS.Proton project) wraps
/// Proton.Sdk's <c>ProtonApiSession</c>; a stub adapter lets the rest of PAWS be developed and
/// tested before the native Proton SDK is built.
/// </summary>
public interface IProtonAuthenticator
{
    Task<ProtonAuthResult> AuthenticateAsync(ProtonLoginRequest request, CancellationToken cancellationToken = default);
}
