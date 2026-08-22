namespace SoulRemote.Models;

/// <summary>
/// State of a single hop in the relay chain (this PC -> Cloudflare edge -> Telegram).
/// Drives both the colour and the motion of the relay line on the dashboard.
/// </summary>
public enum LinkState
{
    /// <summary>Not configured yet.</summary>
    Idle,

    /// <summary>Connecting, deploying or verifying.</summary>
    Working,

    /// <summary>Hop is up and carrying traffic.</summary>
    Online,

    /// <summary>Hop failed; the chain is broken here.</summary>
    Fault,
}
