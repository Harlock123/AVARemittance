namespace AVARemittance.Core.Codes;

/// <summary>
/// The 835's own code lists (ASC X12 005010X221A1), worded as the implementation guide words them: the payment's
/// handling and method, the claim's status and filing indicator, adjustment groups, provider-level adjustment reasons,
/// and the qualifiers of its dates, references, amounts and quantities. The reason and remark codes are
/// <see cref="CodeList"/>'s.
/// </summary>
public static class RemitCodes
{
    public const string Source = "ASC X12 005010X221A1, Health Care Claim Payment/Advice (835)";

    public static readonly IReadOnlyDictionary<string, string> Handling = new Dictionary<string, string>
    {
        ["C"] = "Payment Accompanies Remittance Advice",
        ["D"] = "Make Payment Only",
        ["H"] = "Notification Only",
        ["I"] = "Remittance Information Only",
        ["P"] = "Prenotification of Future Transfers",
        ["U"] = "Split Payment and Remittance",
        ["X"] = "Handling Party's Option to Split Payment and Remittance",
    };

    public static readonly IReadOnlyDictionary<string, string> Method = new Dictionary<string, string>
    {
        ["ACH"] = "Automated Clearing House (ACH)",
        ["BOP"] = "Financial Institution Option",
        ["CHK"] = "Check",
        ["FWT"] = "Federal Reserve Funds/Wire Transfer - Nonrepetitive",
        ["NON"] = "Non-Payment Data",
    };

    public static readonly IReadOnlyDictionary<string, string> Format = new Dictionary<string, string>
    {
        ["CCP"] = "Cash Concentration/Disbursement plus Addenda (CCD+)",
        ["CTX"] = "Corporate Trade Exchange (CTX)",
    };

    /// <summary>CLP02.</summary>
    public static readonly IReadOnlyDictionary<string, string> ClaimStatus = new Dictionary<string, string>
    {
        ["1"] = "Processed as Primary",
        ["2"] = "Processed as Secondary",
        ["3"] = "Processed as Tertiary",
        ["4"] = "Denied",
        ["19"] = "Processed as Primary, Forwarded to Additional Payer(s)",
        ["20"] = "Processed as Secondary, Forwarded to Additional Payer(s)",
        ["21"] = "Processed as Tertiary, Forwarded to Additional Payer(s)",
        ["22"] = "Reversal of Previous Payment",
        ["23"] = "Not Our Claim, Forwarded to Additional Payer(s)",
        ["25"] = "Predetermination Pricing Only - No Payment",
    };

    /// <summary>CLP06.</summary>
    public static readonly IReadOnlyDictionary<string, string> Filing = new Dictionary<string, string>
    {
        ["12"] = "Preferred Provider Organization (PPO)",
        ["13"] = "Point of Service (POS)",
        ["14"] = "Exclusive Provider Organization (EPO)",
        ["15"] = "Indemnity Insurance",
        ["16"] = "Health Maintenance Organization (HMO) Medicare Risk",
        ["17"] = "Dental Maintenance Organization",
        ["AM"] = "Automobile Medical",
        ["CH"] = "Champus",
        ["DS"] = "Disability",
        ["HM"] = "Health Maintenance Organization",
        ["LM"] = "Liability Medical",
        ["MA"] = "Medicare Part A",
        ["MB"] = "Medicare Part B",
        ["MC"] = "Medicaid",
        ["OF"] = "Other Federal Program",
        ["TV"] = "Title V",
        ["VA"] = "Veterans Affairs Plan",
        ["WC"] = "Workers' Compensation Health Claim",
        ["ZZ"] = "Mutually Defined",
    };

    /// <summary>CLP09.</summary>
    public static readonly IReadOnlyDictionary<string, string> Frequency = new Dictionary<string, string>
    {
        ["1"] = "Original",
        ["7"] = "Replacement of Prior Claim",
        ["8"] = "Void/Cancel of Prior Claim",
    };

    /// <summary>CAS01, with what each group means for who pays.</summary>
    public static readonly IReadOnlyDictionary<string, string> Group = new Dictionary<string, string>
    {
        ["CO"] = "Contractual Obligations",
        ["CR"] = "Corrections and Reversals",
        ["OA"] = "Other adjustments",
        ["PI"] = "Payor Initiated Reductions",
        ["PR"] = "Patient Responsibility",
    };

    public static readonly IReadOnlyDictionary<string, string> GroupMeaning = new Dictionary<string, string>
    {
        ["CO"] = "the provider's to write off: not billable to the patient",
        ["CR"] = "a correction or reversal (not used from 5010 on)",
        ["OA"] = "neither the provider's nor the patient's by contract",
        ["PI"] = "the payer's own reduction: not billable to the patient",
        ["PR"] = "the patient's to pay",
    };

    /// <summary>PLB03-1 and the other PLB adjustment identifiers.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderAdjustment = new Dictionary<string, string>
    {
        ["50"] = "Late Charge",
        ["51"] = "Interest Penalty Charge",
        ["72"] = "Authorized Return",
        ["90"] = "Early Payment Allowance",
        ["AH"] = "Origination Fee",
        ["AM"] = "Applied to Borrower's Account",
        ["AP"] = "Acceleration of Benefits",
        ["B2"] = "Rebate",
        ["B3"] = "Recovery Allowance",
        ["BD"] = "Bad Debt Adjustment",
        ["BN"] = "Bonus",
        ["C5"] = "Temporary Allowance",
        ["CR"] = "Capitation Interest",
        ["CS"] = "Adjustment",
        ["CT"] = "Capitation Payment",
        ["CV"] = "Capital Passthru",
        ["CW"] = "Certified Registered Nurse Anesthetist Passthru",
        ["DM"] = "Direct Medical Education Passthru",
        ["E3"] = "Withholding",
        ["FB"] = "Forwarding Balance",
        ["FC"] = "Fund Allocation",
        ["GO"] = "Graduate Medical Education Passthru",
        ["HM"] = "Hemophilia Clotting Factor Supplement",
        ["IP"] = "Incentive Premium Payment",
        ["IR"] = "Internal Revenue Service Withholding",
        ["IS"] = "Interim Settlement",
        ["J1"] = "Nonreimbursable",
        ["L3"] = "Penalty",
        ["L6"] = "Interest Owed",
        ["LE"] = "Levy",
        ["LS"] = "Lump Sum",
        ["OA"] = "Organ Acquisition Passthru",
        ["OB"] = "Offset for Affiliated Providers",
        ["PI"] = "Periodic Interim Payment",
        ["PL"] = "Payment Final",
        ["RA"] = "Retro-activity Adjustment",
        ["RE"] = "Return on Equity",
        ["SL"] = "Student Loan Repayment",
        ["TL"] = "Third Party Liability",
        ["WO"] = "Overpayment Recovery",
        ["WU"] = "Unspecified Recovery",
    };

    public static readonly IReadOnlyDictionary<string, string> Date = new Dictionary<string, string>
    {
        ["036"] = "Coverage expires",
        ["050"] = "Received",
        ["150"] = "Service from",
        ["151"] = "Service to",
        ["232"] = "Statement from",
        ["233"] = "Statement to",
        ["405"] = "Produced",
        ["472"] = "Service",
    };

    public static readonly IReadOnlyDictionary<string, string> Reference = new Dictionary<string, string>
    {
        ["0B"] = "State license number",
        ["0K"] = "Policy form",
        ["1A"] = "Blue Cross provider number",
        ["1B"] = "Blue Shield provider number",
        ["1C"] = "Medicare provider number",
        ["1D"] = "Medicaid provider number",
        ["1G"] = "Provider UPIN",
        ["1H"] = "CHAMPUS identification number",
        ["1J"] = "Facility ID number",
        ["1L"] = "Group or policy number",
        ["1S"] = "Ambulatory patient group (APG) number",
        ["1W"] = "Member identification number",
        ["28"] = "Employee identification number",
        ["2U"] = "Payer identification number",
        ["6P"] = "Group number",
        ["6R"] = "Line item control number",
        ["9A"] = "Repriced claim reference number",
        ["9C"] = "Adjusted repriced claim reference number",
        ["APC"] = "Ambulatory payment classification",
        ["BB"] = "Authorization number",
        ["CE"] = "Class of contract code",
        ["D3"] = "NCPDP pharmacy number",
        ["E9"] = "Attachment code",
        ["EA"] = "Medical record number",
        ["EO"] = "Submitter identification number",
        ["EV"] = "Receiver identifier",
        ["F2"] = "Version code - local",
        ["F8"] = "Original reference number",
        ["G1"] = "Prior authorization number",
        ["G2"] = "Provider commercial number",
        ["G3"] = "Predetermination of benefits number",
        ["HI"] = "Health industry number (HIN)",
        ["HPI"] = "National provider identifier (NPI)",
        ["IG"] = "Insurance policy number",
        ["LU"] = "Location number",
        ["NF"] = "NAIC code",
        ["PQ"] = "Payee identification",
        ["RB"] = "Rate code number",
        ["SY"] = "Social security number",
        ["TJ"] = "Federal taxpayer's identification number",
    };

    public static readonly IReadOnlyDictionary<string, string> Amount = new Dictionary<string, string>
    {
        ["AU"] = "Coverage amount",
        ["B6"] = "Allowed",
        ["D8"] = "Discount amount",
        ["DY"] = "Per day limit",
        ["F5"] = "Patient amount paid",
        ["I"] = "Interest",
        ["KH"] = "Deduction amount (late filing)",
        ["NL"] = "Negative ledger balance",
        ["T"] = "Tax",
        ["T2"] = "Total claim before taxes",
        ["ZK"] = "Federal Medicare or Medicaid payment mandate - category 1",
        ["ZL"] = "Federal Medicare or Medicaid payment mandate - category 2",
        ["ZM"] = "Federal Medicare or Medicaid payment mandate - category 3",
        ["ZN"] = "Federal Medicare or Medicaid payment mandate - category 4",
        ["ZO"] = "Federal Medicare or Medicaid payment mandate - category 5",
    };

    /// <summary>NM101 in the 835's claim loop.</summary>
    public static readonly IReadOnlyDictionary<string, string> Entity = new Dictionary<string, string>
    {
        ["QC"] = "Patient",
        ["IL"] = "Insured",
        ["74"] = "Corrected insured",
        ["82"] = "Rendering provider",
        ["TT"] = "Crossover carrier",
        ["PR"] = "Corrected priority payer",
        ["GB"] = "Other subscriber",
    };

    public static string Say(IReadOnlyDictionary<string, string> list, string code) =>
        list.TryGetValue(code.Trim(), out var text) ? text : $"code {code.Trim()} (not in the 835's list)";
}
