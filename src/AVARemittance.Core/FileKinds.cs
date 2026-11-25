using AVARemittance.Core.Billing;
using AVARemittance.Core.Contracts;
using AVARemittance.Core.Denials;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.X12;

namespace AVARemittance.Core;

/// <summary>What kind of file the program reads a file as, found by its content: the same answer for the window, the agents' list and the headless server.</summary>
public static class FileKinds
{
    /// <summary>"835", "837P", "837I", "277", "charges list", "bank statement", "fee schedule", "payers' limits", or null when it's none of them.</summary>
    public static string? Of(string path)
    {
        try
        {
            if (X12File.LooksLikeX12(path))
            {
                var x12 = X12File.Load(path);
                if (x12.Transactions.Any(t => t.Code == "835")) return "835";
                if (ClaimsReader.Is837(x12)) return ClaimsReader.Read(x12, path).Kind;
                if (Status.StatusReader.Is277(x12)) return "277";
                if (x12.Transactions.Any(t => t.Code == "276")) return "276";
                return null;
            }
            if (BankStatement.LooksLikeOne(path)) return "bank statement";
            if (ChargesList.LooksLikeOne(path)) return "charges list";
            if (FeeSchedule.LooksLikeOne(path)) return "fee schedule";
            if (PayerLimits.LooksLikeOne(path)) return "payers' limits";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
