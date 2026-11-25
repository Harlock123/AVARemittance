#!/usr/bin/env python3
"""Makes the samples: an invented family clinic's claims (837P) and the remittances (835s) its two payers sent back.

    cd samples && python3 make_samples.py

Everything is made up: the clinic, the payers, every patient, member ID, account, NPI, tax ID, bank account, check
and trace number. The output is deterministic, and the tests pin what each file says: plant a new situation here and
pin it in the tests together.

Each claim has one situation planted (see SITUATION below): paid with a copay, paid with a deductible, denied for a
missing authorization, a line denied for a missing modifier, a payment reversed and paid again, a claim the payer
split in two, a claim paid twice, all of it to the deductible, coordination of benefits, timely filing, a bundled
line, medical necessity, and a Medicaid copay. Harborview's second 835 also takes back an old overpayment and pays
interest (PLB). Two claims have no answer yet: one billed three weeks ago, one in September. charges/ holds the same
claims as a charges list (the other way to say what was billed). bank/ holds the clinic's November bank statement, as
a CSV and as BAI2: the three payments' money, a Harborview payment whose 835 hasn't come yet, and patients' card
payments. contracts/ holds the clinic's fee schedule and its payers' limits for appeals. status/ holds the 276 the app
writes for the two claims with no answer, and the 277s that answered it. problems/ holds an 835 that doesn't balance.
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))

CLINIC = {"name": "RIVERBEND FAMILY MEDICINE", "tin": "471234567", "address": ("400 RIVER ROAD", "MILLBROOK", "OR", "97000")}


def npi_check(base9):
    """The NPI's check digit: Luhn over 80840 and the first nine digits."""
    digits = [int(c) for c in "80840" + base9]
    total = 0
    for i, d in enumerate(reversed(digits)):
        if i % 2 == 0:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return base9 + str((10 - total % 10) % 10)


CLINIC["npi"] = npi_check("158866763")
DOCTOR = ("OKONKWO", "ADAEZE", npi_check("172634459"))

PAYERS = {
    "HV": {"name": "HARBORVIEW HEALTH PLAN", "id": "HVHP1", "tin": "1860000001", "filing": "12",
           "address": ("PO BOX 7700", "PORTLAND", "OR", "97208"), "phone": "8005550190", "web": "WWW.HARBORVIEW.EXAMPLE"},
    "TG": {"name": "TALLGRASS MEDICAID", "id": "TGM01", "tin": "1930000002", "filing": "MC",
           "address": ("PO BOX 1500", "SALEM", "OR", "97309"), "phone": "8005550123", "web": "WWW.TALLGRASS.EXAMPLE"},
}

# Each line: (procedure, modifiers, charge, units, adjustments [(group, reason, amount)], remarks, allowed or None).
# A line's payment is its charge less its adjustments.
CLAIMS = {
    # Harborview, paid on 10 November.
    "RB26101": {"payer": "HV", "patient": ("ALVAREZ", "MARCO", "HV800100201"), "dos": "20261020", "situation": "paid, with a copay",
                "lines": [("99213", [], 150, 1, [("CO", "45", 40), ("PR", "3", 25)], [], 110)]},
    "RB26102": {"payer": "HV", "patient": ("BRENNAN", "CLAIRE", "HV800100344"), "dos": "20261021", "situation": "paid, most of it to the deductible",
                "lines": [("99214", [], 220, 1, [("CO", "45", 80), ("PR", "1", 100)], [], 140),
                          ("85025", [], 40, 1, [("CO", "45", 25)], [], 15)]},
    "RB26103": {"payer": "HV", "patient": ("CHO", "DANIEL", "HV800100569"), "dos": "20261022", "situation": "denied: no authorization on the claim",
                "status": "4", "lines": [("72148", [], 1200, 1, [("CO", "197", 1200)], ["M62"], None)]},
    "RB26104": {"payer": "HV", "patient": ("DUBOIS", "ELENA", "HV800100612"), "dos": "20261023", "situation": "the visit denied: modifier 25 missing",
                "lines": [("99214", [], 220, 1, [("CO", "4", 220)], ["N822"], None),
                          ("20610", [], 180, 1, [("CO", "45", 60), ("PR", "2", 24)], [], 120)]},
    "RB26105": {"payer": "HV", "patient": ("ELLIS", "JORDAN", "HV800100733"), "dos": "20261026", "situation": "paid, then reversed and paid again at the new rate",
                "lines": [("99215", [], 300, 1, [("CO", "45", 120), ("PR", "3", 30)], [], 180)],
                "again": [("99215", [], 300, 1, [("CO", "45", 108), ("PR", "3", 30)], [], 192)]},
    "RB26107": {"payer": "HV", "patient": ("GARCIA", "LUIS", "HV800100854"), "dos": "20261027", "situation": "paid twice",
                "lines": [("99213", [], 150, 1, [("CO", "45", 40), ("PR", "3", 25)], [], 110)]},
    "RB26108": {"payer": "HV", "patient": ("HUANG", "MEI", "HV800100975"), "dos": "20261028", "situation": "all of it to the deductible",
                "lines": [("99203", [], 200, 1, [("CO", "45", 70), ("PR", "1", 130)], [], 130)]},
    # Harborview, paid on 17 November: the split claim.
    "RB26106": {"payer": "HV", "patient": ("FOSTER", "GRACE", "HV800101096"), "dos": "20261029", "situation": "split by the payer into two claims",
                "lines": [("99396", [], 250, 1, [("CO", "45", 70)], [], 180),
                          ("90686", [], 40, 1, [("CO", "45", 14)], [], 26),
                          ("90471", [], 30, 1, [("CO", "45", 8)], [], 22)],
                "split": [[0], [1, 2]]},
    # Tallgrass, check of 12 November.
    "RB26110": {"payer": "TG", "patient": ("JOHNSON", "AMARA", "TG55012345"), "dos": "20261019", "situation": "paid at the Medicaid rate",
                "lines": [("99213", [], 150, 1, [("CO", "45", 87.60)], [], 62.40)]},
    "RB26111": {"payer": "TG", "patient": ("KOWALSKI", "PETER", "TG55023456"), "dos": "20261020", "situation": "denied: another payer is primary",
                "status": "4", "lines": [("99214", [], 220, 1, [("CO", "22", 220)], ["MA04"], None)]},
    "RB26112": {"payer": "TG", "patient": ("LOPEZ", "ANA", "TG55034567"), "dos": "20260502", "situation": "denied: filed too late",
                "status": "4", "lines": [("99213", [], 150, 1, [("CO", "29", 150)], [], None)]},
    "RB26113": {"payer": "TG", "patient": ("MBEKI", "THABO", "TG55045678"), "dos": "20261021", "situation": "a line bundled into the visit",
                "lines": [("99214", [], 220, 1, [("CO", "45", 131.90)], [], 88.10),
                          ("36415", [], 15, 1, [("CO", "97", 15)], ["M15"], 0),
                          ("85025", [], 40, 1, [("CO", "45", 31.50)], [], 8.50)]},
    "RB26114": {"payer": "TG", "patient": ("NGUYEN", "LINH", "TG55056789"), "dos": "20261022", "situation": "therapy denied as not medically necessary",
                "lines": [("97110", ["GP"], 160, 4, [("CO", "50", 160)], ["N130"], None),
                          ("97161", ["GP"], 140, 1, [("CO", "45", 61.25)], [], 78.75)]},
    "RB26115": {"payer": "TG", "patient": ("OKAFOR", "CHIDI", "TG55067890"), "dos": "20261023", "situation": "paid, with a Medicaid copay",
                "lines": [("99213", [], 150, 1, [("CO", "45", 87.60), ("PR", "3", 4)], [], 62.40)]},
    # Billed, with no answer yet.
    "RB26116": {"payer": "HV", "patient": ("PATEL", "RAVI", "HV800101217"), "dos": "20261030", "situation": "no answer yet",
                "lines": [("99214", [], 220, 1, [], [], None)]},
    "RB26117": {"payer": "TG", "patient": ("QUINN", "ROSE", "TG55078901"), "dos": "20260922", "situation": "no answer since September",
                "billed": "20260930", "lines": [("99213", [], 150, 1, [], [], None)]},
}

# Each patient's birth date and sex, as the 837 (DMG) and the charges list have them.
BORN = {"RB26101": ("19780412", "M"), "RB26102": ("19850923", "F"), "RB26103": ("19690130", "M"), "RB26104": ("19910617", "F"),
        "RB26105": ("19830305", "M"), "RB26106": ("19760811", "F"), "RB26107": ("19620224", "M"), "RB26108": ("19990719", "F"),
        "RB26110": ("20010502", "F"), "RB26111": ("19580914", "M"), "RB26112": ("19940128", "F"), "RB26113": ("19870606", "M"),
        "RB26114": ("19730321", "F"), "RB26115": ("20051110", "M"), "RB26116": ("19801207", "M"), "RB26117": ("19660415", "F")}

DIAGNOSES = {"99213": "J069", "99214": "I10", "99215": "E119", "99203": "J069", "85025": "I10", "72148": "M5416", "20610": "M25561",
             "99396": "Z0000", "90686": "Z23", "90471": "Z23", "36415": "I10", "97110": "M6281", "97161": "M6281"}

# The old overpayment Harborview takes back on 17 November (a claim from September, paid twice then).
RECOUP = ("HV26090300088", 75.00)
INTEREST = 3.12


def money(x):
    s = f"{x:.2f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s


def line_paid(line):
    return round(line[2] - sum(a[2] for a in line[4]), 2)


class X12:
    def __init__(self):
        self.segs = []

    def add(self, *elements):
        while elements and elements[-1] == "":
            elements = elements[:-1]
        self.segs.append("*".join(str(e) for e in elements))

    def text(self, sender, receiver, date, control, code="835", version="005010X221A1"):
        isa = (f"ISA*00*{'':10}*00*{'':10}*ZZ*{sender:<15}*ZZ*{receiver:<15}*{date[2:]}*0800*^*00501*{control:09d}*0*P*:")
        gs = f"GS*{'HP' if code == '835' else 'HC'}*{sender}*{receiver}*{date}*0800*{control}*X*{version}"
        body = self.segs
        st = [f"ST*{code}*0001" + (f"*{version}" if code != "835" else "")] + body + [f"SE*{len(body) + 2}*0001"]
        out = [isa, gs] + st + [f"GE*1*{control}", f"IEA*1*{control:09d}"]
        return "~\n".join(out) + "~\n"


def svc_segments(x, line, dos, control):
    code, mods, charge, units, adjs, remarks, allowed = line
    proc = ":".join(["HC", code] + mods)
    x.add("SVC", proc, money(charge), money(line_paid(line)), "", money(units) if units != 1 else "1")
    x.add("DTM", "472", dos)
    for group in ("CO", "PI", "OA", "PR"):
        g = [a for a in adjs if a[0] == group]
        if g:
            x.add("CAS", group, *[v for a in g for v in (a[1], money(a[2]))])
    x.add("REF", "6R", control)
    if allowed is not None:
        x.add("AMT", "B6", money(allowed))
    for r in remarks:
        x.add("LQ", "HE", r)


def clp(x, account, claim, lines, status, payer_claim, sign=1, line_numbers=None, original=""):
    payer = PAYERS[claim["payer"]]
    charge = sum(l[2] for l in lines) * sign
    paid = sum(line_paid(l) for l in lines) * sign
    pr = sum(a[2] for l in lines for a in l[4] if a[0] == "PR") * sign
    x.add("CLP", account, status, money(charge), money(paid), money(pr) if pr else "0", payer["filing"], payer_claim, "11", "1")
    last, first, member = claim["patient"]
    x.add("NM1", "QC", "1", last, first, "", "", "", "MI", member)
    x.add("NM1", "82", "1", DOCTOR[0], DOCTOR[1], "", "", "", "XX", DOCTOR[2])
    if original:
        x.add("REF", "F8", original)
    x.add("DTM", "050", claim.get("received", "20261101"))
    for i, l in enumerate(lines):
        if sign < 0:
            l = (l[0], l[1], -l[2], l[3], [(a[0], a[1], -a[2]) for a in l[4]], [], -l[6] if l[6] else None)
        n = (line_numbers or list(range(len(lines))))[i] + 1
        svc_segments(x, l, claim["dos"], f"{account}-{n}")


def header(x, payer, total, method, trace, paid_on, produced):
    p = PAYERS[payer]
    if method == "NON":
        x.add("BPR", "H", money(total), "C", "NON", "", "", "", "", "", "", "", "", "", "", "", paid_on)
    elif method == "ACH":
        x.add("BPR", "I", money(total), "C", "ACH", "CCP", "01", "091000019", "DA", "88001234", p["tin"], "", "01", "123000220", "DA", "5550001234", paid_on)
    else:
        x.add("BPR", "I", money(total), "C", "CHK", "", "", "", "", "", "", "", "", "", "", "", paid_on)
    x.add("TRN", "1", trace, p["tin"])
    x.add("REF", "EV", CLINIC["tin"])
    x.add("DTM", "405", produced)
    x.add("N1", "PR", p["name"])
    x.add("N3", p["address"][0])
    x.add("N4", *p["address"][1:])
    x.add("REF", "2U", p["id"])
    x.add("PER", "BL", "PROVIDER SERVICES", "TE", p["phone"], "UR", p["web"])
    x.add("N1", "PE", CLINIC["name"], "XX", CLINIC["npi"])
    x.add("N3", CLINIC["address"][0])
    x.add("N4", *CLINIC["address"][1:])
    x.add("REF", "TJ", CLINIC["tin"])


def total_of(claims):
    return round(sum(c for c in claims), 2)


def write(name, text):
    path = os.path.join(HERE, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="") as f:
        f.write(text)


def harborview_1110():
    x = X12()
    accounts = ["RB26101", "RB26102", "RB26103", "RB26104", "RB26105", "RB26107", "RB26108"]
    paid = [sum(line_paid(l) for l in CLAIMS[a]["lines"]) for a in accounts]
    header(x, "HV", total_of(paid), "ACH", "1HV2611100001", "20261110", "20261107")
    x.add("LX", "1")
    for i, a in enumerate(accounts):
        c = CLAIMS[a]
        clp(x, a, c, c["lines"], c.get("status", "1"), f"HV2611050{i + 1:04d}")
    write("harborview-835-1110.x12", x.text("HVHP1", "RIVERBEND", "20261107", 1110))


def harborview_1117():
    x = X12()
    e = CLAIMS["RB26105"]
    g = CLAIMS["RB26107"]
    f = CLAIMS["RB26106"]
    paid = [-sum(line_paid(l) for l in e["lines"]), sum(line_paid(l) for l in e["again"])]
    paid += [sum(line_paid(f["lines"][i]) for i in part) for part in f["split"]]
    paid += [sum(line_paid(l) for l in g["lines"])]
    total = round(sum(paid) - RECOUP[1] + INTEREST, 2)
    header(x, "HV", total, "ACH", "1HV2611170002", "20261117", "20261114")
    x.add("LX", "1")
    clp(x, "RB26105", e, e["lines"], "22", "HV26110500005", sign=-1)
    clp(x, "RB26105", e, e["again"], "1", "HV26111200031", original="HV26110500005")
    for n, part in enumerate(f["split"]):
        clp(x, "RB26106", f, [f["lines"][i] for i in part], "1", f"HV2611120004{n + 2}", line_numbers=part)
    clp(x, "RB26107", g, g["lines"], "1", "HV26111200050")
    x.add("PLB", CLINIC["npi"], "20261231", f"WO:{RECOUP[0]}", money(RECOUP[1]), f"L6:HV26111200031", money(-INTEREST))
    write("harborview-835-1117.x12", x.text("HVHP1", "RIVERBEND", "20261114", 1117))


def tallgrass_1112():
    x = X12()
    accounts = ["RB26110", "RB26111", "RB26112", "RB26113", "RB26114", "RB26115"]
    paid = [sum(line_paid(l) for l in CLAIMS[a]["lines"]) for a in accounts]
    header(x, "TG", total_of(paid), "CHK", "40017731", "20261112", "20261110")
    x.add("LX", "1")
    for i, a in enumerate(accounts):
        c = CLAIMS[a]
        clp(x, a, c, c["lines"], c.get("status", "1"), f"TG2611{i + 1:06d}")
    write("tallgrass-835-1112.x12", x.text("TGM01", "RIVERBEND", "20261110", 1112))


def claims_837(payer, billed, accounts, claims=None, born=None, folder=""):
    """The clinic's 837P to a payer: one subscriber per claim (every sample patient is the subscriber)."""
    claims = claims or CLAIMS
    born = born or BORN
    p = PAYERS[payer]
    x = X12()
    x.add("BHT", "0019", "00", f"RB{billed}", billed, "0900", "CH")
    x.add("NM1", "41", "2", CLINIC["name"], "", "", "", "", "46", "RB0001")
    x.add("PER", "IC", "BILLING OFFICE", "TE", "5035550100")
    x.add("NM1", "40", "2", p["name"], "", "", "", "", "46", p["id"])
    x.add("HL", "1", "", "20", "1")
    x.add("NM1", "85", "2", CLINIC["name"], "", "", "", "", "XX", CLINIC["npi"])
    x.add("N3", CLINIC["address"][0])
    x.add("N4", *CLINIC["address"][1:])
    x.add("REF", "EI", CLINIC["tin"])
    for n, a in enumerate(accounts):
        c = claims[a]
        last, first, member = c["patient"]
        x.add("HL", str(n + 2), "1", "22", "0")
        x.add("SBR", "P", "18", "", "", "", "", "", "", "CI" if payer == "HV" else "MC")
        x.add("NM1", "IL", "1", last, first, "", "", "", "MI", member)
        x.add("DMG", "D8", *born[a])
        x.add("NM1", "PR", "2", p["name"], "", "", "", "", "PI", p["id"])
        charge = sum(l[2] for l in c["lines"])
        x.add("CLM", a, money(charge), "", "", "11:B:1", "Y", "A", "Y", "Y")
        codes = []
        for l in c["lines"]:
            if DIAGNOSES[l[0]] not in codes:
                codes.append(DIAGNOSES[l[0]])
        x.add("HI", *[("ABK:" if i == 0 else "ABF:") + d for i, d in enumerate(codes)])
        x.add("NM1", "82", "1", DOCTOR[0], DOCTOR[1], "", "", "", "XX", DOCTOR[2])
        for i, l in enumerate(c["lines"]):
            x.add("LX", str(i + 1))
            x.add("SV1", ":".join(["HC", l[0]] + l[1]), money(l[2]), "UN", money(l[3]), "", "", str(codes.index(DIAGNOSES[l[0]]) + 1))
            x.add("DTP", "472", "D8", c["dos"])
            x.add("REF", "6R", f"{a}-{i + 1}")
    text = x.text("RIVERBEND", p["id"], billed, int(billed[4:]), "837", "005010X222A1")
    write(os.path.join(folder, f"{'harborview' if payer == 'HV' else 'tallgrass'}-837p-{billed[4:]}.x12"), text)


def charges_list():
    rows = ["Account,Payer ID,Payer,Last name,First name,Member ID,Birth date,Sex,Date of service,Procedure,Modifiers,Units,Charge,Billed"]
    for a, c in CLAIMS.items():
        p = PAYERS[c["payer"]]
        last, first, member = c["patient"]
        dos = f"{c['dos'][:4]}-{c['dos'][4:6]}-{c['dos'][6:]}"
        b = c.get("billed", "20261101")
        for l in c["lines"]:
            dob = BORN[a][0]
            rows.append(",".join([a, p["id"], p["name"], last.title(), first.title(), member, f"{dob[:4]}-{dob[4:6]}-{dob[6:]}", BORN[a][1], dos, l[0], " ".join(l[1]),
                                  money(l[3]), f"{l[2]:.2f}", f"{b[:4]}-{b[4:6]}-{b[6:]}"]))
    write(os.path.join("charges", "charges.csv"), "\n".join(rows) + "\n")


# The clinic's bank account in November: the three remittances' money, a Harborview payment whose 835 hasn't come
# yet, patients' card payments, and a fee. (date, amount, description, reference, BAI2 type code)
BANK = [
    ("2026-11-10", 471.00, "ACH CREDIT HARBORVIEW HLTH HCCLAIMPMT RIVERBEND FAMILY TRN*1*1HV2611100001*1860000001\\", "", 165),
    ("2026-11-14", 296.15, "DEPOSIT CHECK 40017731 TALLGRASS MEDICAID", "40017731", 301),
    ("2026-11-17", 253.12, "ACH CREDIT HARBORVIEW HLTH HCCLAIMPMT RIVERBEND FAMILY TRN*1*1HV2611170002*1860000001\\", "", 165),
    ("2026-11-18", 345.00, "MERCHANT DEPOSIT CARD BATCH 1118 PATIENT PAYMENTS", "", 301),
    ("2026-11-20", -15.00, "MONTHLY SERVICE FEE", "", 698),
    ("2026-11-24", 412.50, "ACH CREDIT HARBORVIEW HLTH HCCLAIMPMT RIVERBEND FAMILY TRN*1*1HV2611240003*1860000001\\", "", 165),
]


def bank():
    rows = ["Date,Description,Amount,Reference"]
    for date, amount, words, ref, _ in BANK:
        rows.append(f'{date},"{words}",{amount:.2f},{ref}')
    write(os.path.join("bank", "statement-2026-11.csv"), "\n".join(rows) + "\n")
    # The same as BAI2: one group per day (the 02 record carries the as-of date), amounts in cents.
    lines = ["01,BANKCO,RIVERBEND,261125,0600,1125,80,1,2/"]
    groups = 0
    records = 1
    for date in sorted({b[0] for b in BANK}):
        groups += 1
        day = [b for b in BANK if b[0] == date]
        group = [f"02,RIVERBEND,123000220,1,{date[2:4]}{date[5:7]}{date[8:]},2400,USD,2/", "03,5550001234,USD,010,0,,/"]
        for _, amount, words, ref, code in day:
            group.append(f"16,{code},{abs(round(amount * 100)):d},0,BR{date[5:7]}{date[8:]}{code},{ref},{words}/")
        total = sum(round(b[1] * 100) for b in day)
        group.append(f"49,{total},{len(group) + 1}/")
        group.append(f"98,{total},1,{len(group) + 2}/")
        lines += group
        records += len(group)
    lines.append(f"99,{sum(round(b[1] * 100) for b in BANK)},{groups},{records + 1}/")
    write(os.path.join("bank", "statement-2026-11.bai"), "\n".join(lines) + "\n")


# The clinic's fee schedule: Harborview's contracted rates, and Tallgrass at 65% of a Medicare-style schedule (the
# MEDICARE rows are invented too). Three lines come in below it: Huang's new-patient visit, Foster's preventive visit and
# Mbeki's visit. Harborview raised its 99215 rate from October, which is why it reversed and repaid Ellis's claim.
FEES = [
    ("HVHP1", "99203", "", "140.00", "", "", "", ""),
    ("HVHP1", "99213", "", "110.00", "", "", "", ""),
    ("HVHP1", "99214", "", "140.00", "", "", "", ""),
    ("HVHP1", "99215", "", "180.00", "", "", "", "2026-09-30"),
    ("HVHP1", "99215", "", "192.00", "", "", "2026-10-01", ""),
    ("HVHP1", "85025", "", "15.00", "", "", "", ""),
    ("HVHP1", "20610", "", "120.00", "", "", "", ""),
    ("HVHP1", "72148", "", "650.00", "", "", "", ""),
    ("HVHP1", "99396", "", "195.00", "", "", "", ""),
    ("HVHP1", "90686", "", "26.00", "", "", "", ""),
    ("HVHP1", "90471", "", "22.00", "", "", "", ""),
    ("TGM01", "*", "", "", "65", "MEDICARE", "", ""),
    ("MEDICARE", "99203", "", "110.00", "", "", "", ""),
    ("MEDICARE", "99213", "", "96.00", "", "", "", ""),
    ("MEDICARE", "99214", "", "140.00", "", "", "", ""),
    ("MEDICARE", "85025", "", "13.08", "", "", "", ""),
    ("MEDICARE", "36415", "", "3.00", "", "", "", ""),
    ("MEDICARE", "97110", "GP", "32.00", "", "", "", ""),
    ("MEDICARE", "97161", "GP", "121.15", "", "", "", ""),
]


def fee_schedule():
    rows = ["Payer,Procedure,Modifier,Rate,Percent,Basis,From,To"] + [",".join(r) for r in FEES]
    write(os.path.join("contracts", "fee-schedule.csv"), "\n".join(rows) + "\n")


# Each payer's limits for working denials, in days from the 835's payment date. Invented for the samples: a real
# office takes them from each payer's provider manual.
LIMITS = [
    {"payer": "HVHP1", "name": "HARBORVIEW HEALTH PLAN", "appeal_days": 180, "corrected_claim_days": 90, "timely_filing_days": 90,
     "source": "invented for the samples"},
    {"payer": "TGM01", "name": "TALLGRASS MEDICAID", "appeal_days": 60, "corrected_claim_days": 365, "timely_filing_days": 180,
     "source": "invented for the samples"},
]


def payer_limits():
    import json
    write(os.path.join("contracts", "payer-limits.json"), json.dumps(LIMITS, indent=2) + "\n")


def status_276(asked, now_date="20261124", now_time="0900", control=1, receiver="CLEARINGHOUSE"):
    """The 276 the app writes for the claims with no answer: what AVA Remittance's StatusRequest builds, byte for byte."""
    sender = "RB0001"
    lines = [f"ISA*00*{'':10}*00*{'':10}*ZZ*{sender:<15}*ZZ*{receiver:<15}*{now_date[2:]}*{now_time}*^*00501*{control:09d}*0*P*:",
             f"GS*HR*{sender}*{receiver}*{now_date}*{now_time}*{control}*X*005010X212"]
    payers = []
    for a in asked:
        if CLAIMS[a]["payer"] not in payers:
            payers.append(CLAIMS[a]["payer"])
    for n, payer in enumerate(payers, 1):
        p = PAYERS[payer]
        x = X12()
        x.add("BHT", "0010", "13", f"{sender}{control}{n}", now_date, now_time)
        x.add("HL", "1", "", "20", "1")
        x.add("NM1", "PR", "2", p["name"], "", "", "", "", "PI", p["id"])
        x.add("HL", "2", "1", "21", "1")
        x.add("NM1", "41", "2", CLINIC["name"], "", "", "", "", "46", sender)
        x.add("HL", "3", "2", "19", "1")
        x.add("NM1", "1P", "2", CLINIC["name"], "", "", "", "", "XX", CLINIC["npi"])
        hl = 3
        for a in [a for a in asked if CLAIMS[a]["payer"] == payer]:
            c = CLAIMS[a]
            hl += 1
            last, first, member = c["patient"]
            x.add("HL", str(hl), "3", "22", "0")
            x.add("DMG", "D8", *BORN[a])
            x.add("NM1", "IL", "1", last, first, "", "", "", "MI", member)
            x.add("TRN", "1", f"{a}-{now_date[2:]}")
            x.add("REF", "EJ", a)
            x.add("AMT", "T3", money(sum(l[2] for l in c["lines"])))
            x.add("DTP", "472", "RD8", f"{c['dos']}-{c['dos']}")
        st = f"{n:04d}"
        lines += [f"ST*276*{st}*005010X212"] + x.segs + [f"SE*{len(x.segs) + 2}*{st}"]
    lines += [f"GE*{len(payers)}*{control}", f"IEA*1*{control:09d}"]
    write(os.path.join("status", "riverbend-276-1124.x12"), "~\n".join(lines) + "~\n")


# What each payer's 277 said on 24 November: Harborview has Patel's claim in process; Tallgrass has no record of
# Quinn's, billed in September (send it again, quickly).
STATUS = {"RB26116": ("P1", "20", "HV26111900077"), "RB26117": ("A4", "35", "")}


def status_277(account, now_date="20261124"):
    c = CLAIMS[account]
    p = PAYERS[c["payer"]]
    category, code, payer_claim = STATUS[account]
    last, first, member = c["patient"]
    x = X12()
    x.add("BHT", "0010", "08", f"{p['id']}277{now_date[4:]}", now_date, "1000", "DG")
    x.add("HL", "1", "", "20", "1")
    x.add("NM1", "PR", "2", p["name"], "", "", "", "", "PI", p["id"])
    x.add("HL", "2", "1", "21", "1")
    x.add("NM1", "41", "2", CLINIC["name"], "", "", "", "", "46", "RB0001")
    x.add("HL", "3", "2", "19", "1")
    x.add("NM1", "1P", "2", CLINIC["name"], "", "", "", "", "XX", CLINIC["npi"])
    x.add("HL", "4", "3", "22", "0")
    x.add("NM1", "IL", "1", last, first, "", "", "", "MI", member)
    x.add("TRN", "2", f"{account}-{now_date[2:]}")
    x.add("STC", f"{category}:{code}:PR", now_date, "", money(sum(l[2] for l in c["lines"])), "0")
    if payer_claim:
        x.add("REF", "1K", payer_claim)
    x.add("REF", "EJ", account)
    x.add("DTP", "472", "RD8", f"{c['dos']}-{c['dos']}")
    name = "harborview" if c["payer"] == "HV" else "tallgrass"
    text = x.text(p["id"], "RB0001", now_date, 2400 + int(account[-2:]), "277", "005010X212").replace("GS*HC*", "GS*HN*")
    write(os.path.join("status", f"{name}-277-1124.x12"), text)


def problems():
    """An 835 that doesn't balance: the payment total, a claim, a line and a patient's share are each off, and SE01 is wrong."""
    x = X12()
    header(x, "TG", 100.00, "CHK", "40017999", "20261119", "20261118")
    x.add("LX", "1")
    # A claim whose charge less adjustments isn't its payment (B2), and whose patient share isn't its PR (B5).
    x.add("CLP", "RB26199", "1", "150", "62.4", "10", "MC", "TG2611999001", "11", "1")
    x.add("NM1", "QC", "1", "PRICE", "TESSA", "", "", "", "MI", "TG55099999")
    x.add("SVC", "HC:99213", "150", "62.4", "", "1")
    x.add("DTM", "472", "20261024")
    # The line's adjustments leave 64.40, not the 62.40 paid (B3).
    x.add("CAS", "CO", "45", "85.6")
    x.add("AMT", "B6", "62.4")
    n = len(x.segs) + 2
    # SE01 one more than the count (E1).
    text = x.text("TGM01", "RIVERBEND", "20261118", 1199).replace(f"SE*{n}*0001", f"SE*{n + 1}*0001")
    write(os.path.join("problems", "unbalanced-835.x12"), text)


# Denials a fresh 271 answers, kept in recheck/ (out of samples/ itself, so the payers' November files stay as they
# are): Tallgrass denies three claims billed on 5 November, on an 835 of 19 November with nothing paid. AVA
# Remittance's Check Eligibility Again writes them as AVA Eligibility's appointment list (recheck/eligibility-recheck.csv).
RECHECK = {
    "RB26118": {"payer": "TG", "patient": ("SANTOS", "ISABEL", "TG55089012"), "dos": "20261103", "situation": "denied: coverage ended",
                "status": "4", "received": "20261106", "lines": [("99213", [], 150, 1, [("CO", "27", 150)], [], None)]},
    "RB26119": {"payer": "TG", "patient": ("TRAN", "VIET", "TG55090123"), "dos": "20261104", "situation": "denied: the patient not found",
                "status": "4", "received": "20261106", "lines": [("99214", [], 220, 1, [("CO", "31", 220)], [], None)]},
    "RB26120": {"payer": "TG", "patient": ("VEGA", "SOFIA", "TG55091234"), "dos": "20261105", "situation": "denied: before coverage began",
                "status": "4", "received": "20261106", "lines": [("99203", [], 200, 1, [("CO", "26", 200)], [], None)]},
}
RECHECK_BORN = {"RB26118": ("19790815", "F"), "RB26119": ("19920203", "M"), "RB26120": ("20000627", "F")}


def carc(code):
    with open(os.path.join(HERE, "..", "src", "AVARemittance.Core", "Codes", "carc.tsv")) as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if parts[0] == code:
                return parts[3]
    raise KeyError(code)


def recheck():
    claims_837("TG", "20261105", list(RECHECK), RECHECK, RECHECK_BORN, "recheck")
    x = X12()
    header(x, "TG", 0, "NON", "40017790", "20261119", "20261118")
    x.add("LX", "1")
    for i, (a, c) in enumerate(RECHECK.items()):
        clp(x, a, c, c["lines"], c["status"], f"TG2611{i + 40:06d}")
    write(os.path.join("recheck", "tallgrass-835-1119.x12"), x.text("TGM01", "RIVERBEND", "20261118", 1119))
    # What the app writes for them: AVA Eligibility's appointment list, the date of service as the visit date.
    rows = ["Date,Last name,First name,Birth date,Sex,Payer ID,Payer,Member ID,Subscriber,Provider,Provider NPI,Visit,Procedure,Charge,Account,Denial"]
    p = PAYERS["TG"]
    for a, c in RECHECK.items():
        last, first, member = c["patient"]
        dob, sex = RECHECK_BORN[a]
        line = c["lines"][0]
        denial = " ".join(f"{g}-{r}: {carc(r)}" for g, r, _ in line[4])
        rows.append(",".join([f"{c['dos'][:4]}-{c['dos'][4:6]}-{c['dos'][6:]}", last, first, f"{dob[:4]}-{dob[4:6]}-{dob[6:]}", sex, p["id"], p["name"], member,
                              "self", CLINIC["name"], CLINIC["npi"], "Denied claim", line[0], f"{line[2]:.2f}", a, denial]))
    write(os.path.join("recheck", "eligibility-recheck.csv"), "\n".join(rows) + "\n")


if __name__ == "__main__":
    harborview_1110()
    harborview_1117()
    tallgrass_1112()
    problems()
    claims_837("HV", "20261101", [a for a, c in CLAIMS.items() if c["payer"] == "HV" and "billed" not in c])
    claims_837("TG", "20261101", [a for a, c in CLAIMS.items() if c["payer"] == "TG" and "billed" not in c])
    claims_837("TG", "20260930", ["RB26117"])
    charges_list()
    bank()
    fee_schedule()
    payer_limits()
    status_276(["RB26116", "RB26117"])
    status_277("RB26116")
    status_277("RB26117")
    recheck()
    for c in CLAIMS.values():
        assert all(round(l[2] - sum(a[2] for a in l[4]), 2) >= 0 for l in c["lines"])
    print("made the samples")
