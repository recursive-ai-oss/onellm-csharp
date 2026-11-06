using OneLlm;
using OneLlm.Pricing;

namespace OneLlm.LiveSmoke;

/// <summary>
/// One provider's spend cap for the run. Before a call it takes the call's worst-case cost (catalog prices, the request's output limit and a
/// margin on the input count) and refuses the call, without sending it, if the spend so far plus that worst case would pass the cap.
/// A call whose cost cannot be bounded is refused too. After the call the real cost replaces the reservation.
/// </summary>
public sealed class SmokeBudget
{
    private readonly SpendGuard _guard;

    /// <summary>Creates a budget.</summary>
    /// <param name="provider">The provider's name, for messages.</param>
    /// <param name="cap">The most the run may spend on this provider, in US dollars.</param>
    /// <param name="alreadySpent">Spend already counted against the cap.</param>
    public SmokeBudget(string provider, decimal cap, decimal alreadySpent = 0m)
    {
        Provider = provider;
        Cap = cap;
        _guard = new SpendGuard(spendCap: cap, alreadySpent: alreadySpent);
    }

    /// <summary>The provider's name.</summary>
    public string Provider { get; }

    /// <summary>The cap in US dollars.</summary>
    public decimal Cap { get; }

    /// <summary>What has been spent so far.</summary>
    public decimal Spent => _guard.Spent;

    /// <summary>Sets a call's worst case aside if it fits under the cap.</summary>
    /// <param name="estimate">The call's estimate.</param>
    /// <param name="reserved">The amount set aside, to pass to <see cref="Settle"/>.</param>
    /// <param name="reason">Why the call was refused; null when it fits.</param>
    /// <returns>True when the call may be sent.</returns>
    public bool TryReserve(CostEstimate estimate, out decimal reserved, out string? reason)
    {
        try
        {
            reserved = _guard.Reserve(estimate);
            reason = null;
            return true;
        }
        catch (BudgetExceededException e)
        {
            reserved = 0m;
            reason = e.Message;
            return false;
        }
    }

    /// <summary>Ends a reservation with the call's real cost; a null cost (unknown price) is charged at the reserved worst case.</summary>
    /// <param name="reserved">What <see cref="TryReserve"/> set aside.</param>
    /// <param name="actual">The real cost, or null when it is not known.</param>
    public void Settle(decimal reserved, decimal? actual) => _guard.Settle(reserved, actual ?? reserved);

    /// <summary>Ends a reservation for a call that failed before any billing could happen.</summary>
    /// <param name="reserved">What <see cref="TryReserve"/> set aside.</param>
    public void Release(decimal reserved) => _guard.Release(reserved);
}
