using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.X12;

namespace AVARemittance;

/// <summary>The hooks later parts of the window fill: what each milestone adds stays out of MainWindow.cs.</summary>
public sealed partial class MainWindow
{
    private partial IEnumerable<TabItem> MoreTabs() => [ClaimsTab(), DepositsTab(), UnderTab(), DenialsTab(), StatusTab()];

    private partial IEnumerable<object> MoreFileItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        item("Export the _Posting Summary…", () => _ = ExportPosting(), null, KeyModifiers.None),
        item("Export the _Denials…", () => _ = ExportDenials(), null, KeyModifiers.None),
        new Separator(),
        item("Check _Eligibility Again for the Coverage Denials…", () => _ = CheckEligibilityAgain(null), null, KeyModifiers.None),
        item("Where AVA Eligibility _Is…", () => _ = PickEligibilityProgram(), null, KeyModifiers.None),
        new Separator(),
    ];

    private partial IEnumerable<object> MoreViewItems(Func<string, Action, Key?, KeyModifiers, MenuItem> item) =>
    [
        item("_Deposits", () => Tabs.SelectedIndex = 3, Key.D4, KeyModifiers.Control),
        item("_Underpayments", () => Tabs.SelectedIndex = 4, Key.D5, KeyModifiers.Control),
        item("De_nials", () => Tabs.SelectedIndex = 5, Key.D6, KeyModifiers.Control),
        item("Claim _Status", () => Tabs.SelectedIndex = 6, Key.D7, KeyModifiers.Control),
    ];

    private partial bool Takes(string path) => IsChargesList(path) || IsStatement(path) || IsSchedule(path) || IsLimits(path);

    private partial void OpenOther(string path)
    {
        if (IsStatement(path)) OpenStatement(path);
        else if (IsChargesList(path)) OpenChargesList(path);
        else if (IsLimits(path)) OpenLimits(path);
        else OpenSchedule(path);
    }

    private partial bool OpenX12(string path, X12File x12) => OpenBilledX12(path, x12) || OpenStatusX12(path, x12);
    private partial (string, IBrush)? MarkOf(ClaimPayment c) => MatchMark(c);

    partial void AfterBuild()
    {
        AgentsReady();
        FillClaimSummary();
        FillDeposits();
        FillUnder();
        FillWorkSummary();
        FillStatus();
    }

    partial void FilesChanged()
    {
        Rematch();
        Rebank();
        Recheck();
        Rework();
    }

    partial void AgentsReady();

    partial void Rematched()
    {
        FillDeposits();
        FillStatus();
    }
}
