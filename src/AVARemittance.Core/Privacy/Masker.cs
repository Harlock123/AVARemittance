using System.Text;
using System.Text.RegularExpressions;
using AVARemittance.Core.Billing;
using AVARemittance.Core.Deposits;
using AVARemittance.Core.Remittance;
using AVARemittance.Core.Status;

namespace AVARemittance.Core.Privacy;

/// <summary>
/// People's details as AI agents see them unless the person allows otherwise. Every name, member ID, patient account,
/// payer claim number, line control number, medical record number, check and trace number and bank account in the open
/// files is known, so it's masked wherever it turns up (a finding, a deposit's description, a raw segment): to its
/// shape, the first character kept, other letters as *, digits as 9 (RB26104 is R*99999, HV800100612 is H*999999999).
/// Every date keeps only its year. Payers, providers, codes and amounts aren't people's details and are kept.
/// AVA Eligibility's, with this app's files: fix them together.
/// </summary>
public sealed class Masker
{
    private static readonly Regex IsoDate = new(@"\b((?:19|20)\d\d)-\d\d-\d\d\b", RegexOptions.CultureInvariant);
    private static readonly Regex CompactDate = new(@"(?<![0-9])((?:19|20)\d\d)(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?![0-9])", RegexOptions.CultureInvariant);
    private static readonly Regex SlashDate = new(@"\b\d{1,2}/\d{1,2}/((?:19|20)\d\d)\b", RegexOptions.CultureInvariant);
    private static readonly Regex RangeDate = new(@"(?<![0-9])((?:19|20)\d\d)(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])-((?:19|20)\d\d)(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?![0-9])", RegexOptions.CultureInvariant);

    private readonly List<(Regex Pattern, string Masked)> _tokens;

    public Masker(IEnumerable<RemitFile> remits, IEnumerable<BilledFile>? billed = null, IEnumerable<BankStatement>? statements = null, IEnumerable<StatusAnswer>? statuses = null)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? v)
        {
            v = v?.Trim();
            if (v is { Length: >= 2 }) values.Add(v);
        }
        void Name(string last, string first, string middle = "")
        {
            Add(last);
            Add(first);
            Add(middle);
            foreach (var part in $"{last} {first} {middle}".Split([' ', '-', ',', '\''], StringSplitOptions.RemoveEmptyEntries)) if (part.Length >= 3) Add(part);
        }
        foreach (var p in remits.SelectMany(f => f.Payments))
        {
            Add(p.Trace);
            Add(p.SenderAccount);
            Add(p.ReceiverAccount);
            foreach (var c in p.Claims)
            {
                Add(c.Account);
                Add(c.PayerClaim);
                foreach (var who in new[] { c.Patient, c.Insured, c.Corrected })
                    if (who != null) { Name(who.Last, who.First, who.Middle); Add(who.Id); }
                foreach (var r in c.References) Add(r.Value);
                foreach (var l in c.Lines) foreach (var r in l.References) Add(r.Value);
            }
            foreach (var a in p.ProviderAdjustments) Add(a.Reference);
        }
        foreach (var c in billed?.SelectMany(b => b.Claims) ?? [])
        {
            Add(c.Account);
            Name(c.Last, c.First);
            Add(c.MemberId);
            Add(c.Original);
            foreach (var l in c.Lines) Add(l.ControlNumber);
        }
        foreach (var d in statements?.SelectMany(s => s.Deposits) ?? [])
        {
            Add(d.Trace);
            Add(d.Reference);
        }
        foreach (var a in statuses ?? [])
        {
            Name(a.Last, a.First);
            Add(a.MemberId);
            Add(a.Account);
            Add(a.PayerClaim);
            Add(a.Trace);
        }
        _tokens = [.. values.OrderByDescending(v => v.Length).Select(v => (new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(v)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Shape(v)))];
    }

    /// <summary>A value's shape: the first character kept, other letters as *, digits as 9, the rest kept.</summary>
    public static string Shape(string value)
    {
        var b = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            b.Append(i == 0 ? c : char.IsLetter(c) ? '*' : char.IsDigit(c) ? '9' : c);
        }
        return b.ToString();
    }

    /// <summary>Text with every person's detail masked and every date reduced to its year.</summary>
    public string Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var (pattern, masked) in _tokens) text = pattern.Replace(text, masked);
        text = RangeDate.Replace(text, m => $"{m.Groups[1].Value}****-{m.Groups[2].Value}****");
        text = IsoDate.Replace(text, m => $"{m.Groups[1].Value}-**-**");
        text = CompactDate.Replace(text, m => $"{m.Groups[1].Value}****");
        text = SlashDate.Replace(text, m => $"**/**/{m.Groups[1].Value}");
        return text;
    }

    /// <summary>A date as agents see it: its year.</summary>
    public static string Year(DateOnly? d) => d is { } x ? $"{x.Year}-**-**" : "";
}
