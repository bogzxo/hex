namespace Hex.Core;

/// <summary>
/// How edit prediction stands, for the status bar: working, switched off, waiting for an answer, or what went wrong in
/// a word the user can act on.
/// </summary>
public enum PredictionStatus
{
    Ok,
    Off,
    Waiting,
    Unreachable,
    Unauthorized,
    NoSuchModel,
    TimedOut,
    BadAnswer,
    Refused,
}
