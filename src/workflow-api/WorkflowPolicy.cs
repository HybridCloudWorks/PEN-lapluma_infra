namespace LaPluma.WorkflowApi;

/// <summary>
/// Domain workflow policies governing separation of duties and lifecycle state transitions.
/// </summary>
public static class WorkflowPolicy
{
    /// <summary>
    /// Three distinct actors are required for preparation, review, and approval.
    /// An approver cannot have prepared or reviewed the case.
    /// </summary>
    public static bool CanApprove(string? preparerId, string? reviewerId, string? approverId)
    {
        if (string.IsNullOrWhiteSpace(approverId))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preparerId) &&
            string.Equals(approverId, preparerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(reviewerId) &&
            string.Equals(approverId, reviewerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(preparerId) && !string.IsNullOrWhiteSpace(reviewerId) &&
            string.Equals(preparerId, reviewerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Valid case state machine transitions.
    /// </summary>
    public static bool AllowedTransition(string fromState, string toState)
    {
        var from = fromState.ToUpperInvariant();
        var to = toState.ToUpperInvariant();

        return (from, to) switch
        {
            ("DRAFT", "COLLECTING") => true,
            ("COLLECTING", "VALIDATING") => true,
            ("VALIDATING", "IN_PROGRESS") => true,
            ("VALIDATING", "IN_REVIEW") => true,
            ("IN_PROGRESS", "VALIDATING") => true,
            ("IN_PROGRESS", "IN_REVIEW") => true,
            ("IN_REVIEW", "CHANGES_REQUESTED") => true,
            ("IN_REVIEW", "READY_FOR_APPROVAL") => true,
            ("CHANGES_REQUESTED", "IN_REVIEW") => true,
            ("CHANGES_REQUESTED", "IN_PROGRESS") => true,
            ("READY_FOR_APPROVAL", "APPROVED") => true,
            ("READY_FOR_APPROVAL", "IN_PROGRESS") => true,
            ("READY_FOR_APPROVAL", "CHANGES_REQUESTED") => true,
            ("APPROVED", "GENERATED") => true,
            ("APPROVED", "IN_PROGRESS") => true, // invalidation path
            ("GENERATED", "DELIVERED") => true,
            ("DELIVERED", "CLOSED") => true,
            // Edition drift freeze transitions
            (_, "QUARANTINED_FORM_DRIFT") => true,
            ("QUARANTINED_FORM_DRIFT", "COLLECTING") => true,
            _ => false
        };
    }
}
