namespace Hex.Dawn;

/// <summary>
/// What the Dock icon of a Mac should be told, from how many approvals and questions wait for the person and whether a task
/// ended: the badge is the count of approvals (nothing at 0), a new approval bounces the icon once, a new question until it is
/// looked at (it is addressed to the person), and a task that ended bounces it once. Pure, the editor's thread feeds it every tick
/// and tells the system what it answers.
/// </summary>
internal sealed class MacDockState
{
    private static int taskEnded;

    private int approvals, questions;
    private string? badge;

    /// <summary>A task ended, from any thread. Said to the Dock on the next tick.</summary>
    internal static void NoteTaskEnded() => Interlocked.Exchange(ref taskEnded, 1);

    /// <summary>The text on the icon as last answered.</summary>
    internal string? Badge => badge;

    /// <summary>
    /// What to tell the system for how things are now. <c>BadgeChanged</c> says the badge is to be set to <c>Badge</c>,
    /// <c>Critical</c> and <c>Informational</c> that the icon is to bounce.
    /// </summary>
    internal (bool BadgeChanged, string? Badge, bool Critical, bool Informational) Update(int approvalsNow, int questionsNow, bool taskEndedNow)
    {
        string? label = approvalsNow > 0 ? approvalsNow.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        bool changed = label != badge;
        badge = label;

        bool critical = questionsNow > questions;
        bool informational = !critical && (approvalsNow > approvals || taskEndedNow);
        approvals = approvalsNow;
        questions = questionsNow;
        return (changed, label, critical, informational);
    }

    /// <summary>Update, taking whether a task ended since the last one.</summary>
    internal (bool BadgeChanged, string? Badge, bool Critical, bool Informational) Update(int approvalsNow, int questionsNow) =>
        Update(approvalsNow, questionsNow, Interlocked.Exchange(ref taskEnded, 0) != 0);
}
