namespace PAWS.Core.Proton;

public enum ProtonAuthStatus
{
    Success,
    Error,
}

/// <summary>Outcome of an authentication attempt.</summary>
public sealed class ProtonAuthResult
{
    public ProtonAuthStatus Status { get; init; }
    public ProtonSession? Session { get; init; }
    public string? Message { get; init; }

    public bool IsSuccess => Status == ProtonAuthStatus.Success && Session is not null;

    public static ProtonAuthResult Success(ProtonSession session) =>
        new() { Status = ProtonAuthStatus.Success, Session = session };

    public static ProtonAuthResult Failure(ProtonAuthStatus status, string message) =>
        new() { Status = status, Message = message };
}
