# AVA Remittance

Know what the payer paid, and why. AVA Remittance reads HIPAA **835** remittances (005010X221A1) and says, claim by
claim and line by line, what was billed, allowed, paid, adjusted off and left to the patient, in plain words. Every
adjustment and remark code is read from X12's published lists.

It checks that each 835 balances: the payment against its claims and the payer's provider-level adjustments
(recoupments, interest), each claim against its adjustments and lines, and each line against its own.

## Reading a remittance

Open 835s (File › Open, or drop them on the window). The list shows each payment, with the claims it pays, each marked
paid, partly denied, denied, patient owes or reversal. Choose a payment to see the money, the trace or check number,
the provider-level adjustments and whether it balances; choose a claim to see each line and each adjustment:

> CO-45 $40.00: Charge exceeds fee schedule/maximum allowable or contracted/legislated fee arrangement. (contractual
> obligations: the provider's to write off: not billable to the patient)

Clicking a line highlights the segments it came from. The **Checks** tab lists, for every open 835, whatever doesn't
balance, with the rule and its source.

## What was billed, and what came back

Open the **837s** you sent (837P or 837I), or a **charges list**: a CSV or Excel file, one row per line, with columns
found by name (Account, Payer ID, Payer, Last name, First name, Member ID, Date of service, Procedure, Modifiers,
Units, Charge, Billed). The **Claims** tab matches each billed claim to its payments, by the account the 835 sends
back, or else by patient, date and charge, and each line by its control number or its code and date. Every claim is
marked:

- **paid**, **partly paid**, **denied**, or **patient owes** (all of it to the deductible);
- **reversed**: the payment was taken back with nothing in its place yet; a reversal followed by a new payment is a
  correction, and says what changed;
- **paid twice**: the same line paid in two payments, and how much too much;
- **no answer**, with the days since it was billed.

A claim the payer split into two shows as one, with both payments. Each line says what paid it, or that it was
bundled into another. Claims in the 835s that nothing open billed are listed too.

## The money

Open a **bank statement**: a CSV or Excel file (Date, Description, Amount or Credit and Debit, Reference), or the
BAI2 file most banks give. The **Deposits** tab matches each deposit to its 835, by the trace number the EFT carries
(the TRN in its addenda) or the check number, or else by amount within a week. It lists:

- insurance money with no 835 (and its trace, to ask for the 835);
- 835 payments with no deposit;
- a deposit that differs from its 835, and when the difference is the size of a recoupment or interest, which one.

Below it is the **posting summary** by day: received, posted to claims, unapplied (paid on claims nothing open
billed), adjusted off, the patients' share, recouped, interest added and deposited. **File › Export the Posting
Summary** writes it, with every claim, as an HTML page or an Excel workbook.

## Underpayments

Open a **fee schedule**: a CSV or Excel file with Payer, Procedure, Modifier and Rate (and From and To when a rate
changes), or a row with Procedure `*`, a Percent and a Basis for a payer that pays a share of another schedule
(65 of MEDICARE). The **Underpayments** tab flags every line allowed more than the tolerance below what the
schedule expects, and says which rows it rests on. It also flags lines whose own amounts don't agree (allowed, paid
and the patient's share), and a contractual write-down larger than the schedule explains. Below that is each payer's
total below the schedule, by procedure.

A flag says "below what the schedule expects", never "wrong": the schedule is yours, and the contract decides.

## Samples

`samples/` holds an invented family clinic's 837Ps and the 835s its two invented payers sent back, made by
`make_samples.py`; `charges/` has the same claims as a charges list, `bank/` the clinic's November statement as CSV and BAI2, and `contracts/` its fee schedule. Each claim has one situation planted: a copay, a deductible, a missing authorization, a missing modifier, a reversal and
its correction, a claim the payer split, a claim paid twice, coordination of benefits, timely filing, a bundled line,
medical necessity, no answer yet. `problems/` holds an 835 that doesn't balance.

## Building

```bash
dotnet build AVARemittance.slnx
dotnet test AVARemittance.slnx
cd samples && python3 make_samples.py      # the samples
python3 tools/make_codes.py                # the CARC, RARC and 277 status lists, from x12.org
```

.NET 10 and Avalonia 11.3. `AGENTS.md` has how the code is put together.

## License

MIT
