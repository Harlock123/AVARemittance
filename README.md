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

## Samples

`samples/` holds an invented family clinic's remittances from two invented payers, made by `make_samples.py`. Each
claim has one situation planted: a copay, a deductible, a missing authorization, a missing modifier, a reversal and
its correction, a claim the payer split, a claim paid twice, coordination of benefits, timely filing, a bundled line,
medical necessity. `problems/` holds an 835 that doesn't balance.

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
