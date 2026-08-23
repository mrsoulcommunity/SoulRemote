namespace SoulRemote.Abstractions;

/// <summary>
/// The current time, injectable so anything that expires — a prompt, a rate-limit
/// window, a pairing lockout — can be tested without sleeping.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;
}
