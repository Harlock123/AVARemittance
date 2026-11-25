# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers what the program does; this file covers
how the code is put together and the rules that keep it working.

## What this is

An 835 remittance and denials workbench in C# on .NET 10 and Avalonia 11.3: it reads 835s (005010X221A1) and says
what each payment, claim and line means in plain words, checks that each 835 balances, and matches the claims billed
(837s or a charges list) to their payments and the payments to the bank's deposits, and flags lines allowed below the
person's fee schedule, and keeps the denials as a worklist with deadlines, appeal drafts and corrected-claim changes.

## Commands

```bash
dotnet build AVARemittance.slnx
dotnet test AVARemittance.slnx
dotnet run --project src/AVARemittance [files or folders]
cd samples && python3 make_samples.py        # remakes the samples (deterministic: tests pin what each says)
python3 tools/make_codes.py [FOLDER]         # remakes Core/Codes/*.tsv from x12.org's pages (or saved copies)
```

`dotnet test | head` stalls: write the output to a file and grep it.

## Layout

- `src/AVARemittance.Core/`: no Avalonia.
  - `X12/`: `Segment`, `Delimiters`, `X12File`: X12 Explorer's (`~/RiderProjects/X12_820Explorer`), by way of
    AVAClaimScrubber and AVAEligibility, with the namespace changed. Fix them together.
  - `Codes/`: `CodeList` (CARC, RARC, 277 status category and status codes, from the `.tsv` files
    `tools/make_codes.py` makes from x12.org: the lists' own wording, usage notes left off, with start and stop dates),
    `RemitCodes` (the 835's own lists: BPR handling and method, CLP02 status, CLP06 filing, CAS groups and what each
    means for who pays, PLB reasons, DTM, REF, AMT and NM1 qualifiers).
  - `Remittance/`: `RemitModel` (`RemitFile`, `Payment`, `ClaimPayment`, `ServicePayment`, `Adjustment`, `Remark`,
    `ProviderAdjustment`; every item keeps the segments behind it; `ClaimState`; `Money`), `RemitReader` (the header
    to the first LX or CLP, N1 PR and PE, CLP loops, SVC loops, PLB), `Checks` (the balancing rules B1 to B7 and E1,
    each with an id, a why and a source), `Words` (each part in plain words, the same everywhere).
  - `Billing/`: `Table` (a CSV or Excel file read by its header names: the charges list, and later the bank statement
    and fee schedule), `Billed` (`BilledClaim`, `BilledLine`, `BilledFile`; `ClaimsReader`, an 837P or 837I as far as
    matching needs it: payer, subscriber or patient, CLM, SV1/SV2, DTP 472, REF 6R; `ChargesList`, one row per line).
  - `Matching/Matching.cs`: `Matcher` (by account at the same payer, then patient, date and charge; lines by REF 6R,
    then procedure, modifiers and date, or SVC06's billed code), `ClaimMatch` (the answers oldest first; `Standing`
    leaves out what a reversal took back; `BilledState`: paid, partly paid, denied, patient owes, reversed, no answer,
    paid twice; a split claim), `LineMatch`, `Reconciliation` (with `Unbilled`, the 835 claims nothing open billed,
    and the day waiting is counted to), `MatchWords`.
  - `Deposits/`: `Deposits.cs` (`BankStatement`: a CSV or Excel statement by its header names, or BAI2, 02 groups'
    as-of dates and 16/88 records, funds types S, V and D; credits only; the trace from the CCD+ addenda's TRN*1*, or
    a check number; `DepositMatcher`: by trace or check number, then amount within a week, then one deposit for
    several 835s; a difference the size of a PLB is named), `Posting.cs` (`PostingDay`: a day's received, posted,
    unapplied, adjusted, patients', recouped, added and deposited).
  - `Contracts/`: `FeeSchedule` (rates by payer, procedure, modifier and dates, or a percent of a basis's rates;
    `For(line)` says what it expects and which rows it rests on), `Underpayments` (U1 allowed below the schedule, U2
    the payer's own amounts don't agree, U3 a CO-45 larger than the schedule explains; reversed payments left out;
    `ByCode`).
  - `Denials/`: `Denials.cs` (`Classifier`: each adjustment's `WorkAction` from its CARC, group and remarks, and what
    to do in words; CO-45 and its kin and routine patient shares make no work; `Worklist.Build`: one `WorkItem` per
    claim and action, W01… sorted by deadline; `PayerLimits`, the person's JSON of appeal, corrected-claim and timely
    filing days; `WorkStore`, the state in remittance-work.json beside the 835s, keyed by payer, payer claim, lines
    and action), `Letters.cs` (the appeal draft as Markdown, HTML or Word, with [brackets] for what a person adds;
    a corrected claim's changes).
  - `Reports/`: `PostingReport` (the posting summary) and `DenialsReport` (the worklist), HTML or Excel, people's
    details through a mask.
- `src/AVARemittance/`: the window, built in code (`MainWindow.cs`: the list, the Remittance tab, the Checks tab and
  the segments; `MainWindow.Claims.cs`: the Claims tab, matching again whenever a file opens or closes;
  `MainWindow.Deposits.cs`: the Deposits tab and Export the Posting Summary; `MainWindow.Contracts.cs`: the
  Underpayments tab and the tolerance; `MainWindow.Denials.cs`: the Denials tab, recording what was done, the drafts
  and Export the Denials;
  `MainWindow.Parts.cs`: the hooks later parts fill; `Settings.cs`: the settings file, in
  `MainWindow.SettingsFolder`, which tests point at a temporary folder).
- `tests/AVARemittance.Tests/`: xUnit v3 on Avalonia.Headless.
- `samples/`: an invented clinic's 837Ps and the 835s its two invented payers sent back, from `make_samples.py`; each
  claim has one situation planted (see `CLAIMS` there); `charges/` the same claims as a charges list; `bank/` the
  November bank statement as CSV and BAI2 (see `BANK`); `contracts/` the fee schedule (see `FEES`) and
  the payers' limits (see `LIMITS`, invented and labelled so). `problems/` holds an 835 that doesn't balance (kept out of `samples/` itself so
  opening the folder opens the payers' files).

## Rules that keep it working

- **Core stays free of Avalonia.**
- **The samples are pinned.** `ReaderTests` and `ChecksTests` say what each sample claim is and what the checks find:
  plant a new situation in `make_samples.py` and pin it together. The payers' samples balance; only `problems/` doesn't.
- **Words come from `Words`, `CodeList` and `RemitCodes`**, so the window, the reports and agents say the same thing.
  A code a list hasn't got is shown as not in the list, never guessed. Change `Codes/*.tsv` only by running
  `tools/make_codes.py`.
- **Every check has an id, a why and a source**, and a test says so.
- **Underpayments are below what the schedule expects, never wrong.** The schedule is the person's; every flag names
  the rows it rests on.
- **Drafts are drafts, and nothing is sent.** An appeal letter says it's a draft at the top and marks in [brackets]
  what only a person can add; a corrected claim is a list of changes, not an edited 837. Both are files the person
  saves.
- **The worklist's state is the person's record**, in remittance-work.json beside the 835s; tests that change it work
  on `Samples.Copy()`.
- **Days are counted to `Today`**, which the window and tests set (tests use 2026-11-24), never to the clock directly.
- **A window test that fails leaves its window open, and Avalonia's reset then fails with `fonts:SystemFonts`,** which
  hides the real failure: close windows in `finally`.
- **No real data in the repository.** Every person, payer, account, member ID, NPI, tax ID, bank account, check and
  trace number in the samples is invented.

## Making a change

- Run `dotnet test` after any change.
- Pushes to `main` publish builds to the rolling `latest` pre-release; a `v*` tag makes a proper release.
