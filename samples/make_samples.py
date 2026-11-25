#!/usr/bin/env python3
"""Makes the samples: an invented family clinic's remittances (835s) from its two payers.

    cd samples && python3 make_samples.py

Everything is made up: the clinic, the payers, every patient, member ID, account, NPI, tax ID, bank account, check
and trace number. The output is deterministic, and the tests pin what each file says: plant a new situation here and
pin it in the tests together.

Each claim has one situation planted (see SITUATION below): paid with a copay, paid with a deductible, denied for a
missing authorization, a line denied for a missing modifier, a payment reversed and paid again, a claim the payer
split in two, a claim paid twice, all of it to the deductible, coordination of benefits, timely filing, a bundled
line, medical necessity, and a Medicaid copay. Harborview's second 835 also takes back an old overpayment and pays
interest (PLB). problems/ holds an 835 that doesn't balance, for the checks.
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
}

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

    def text(self, sender, receiver, date, control):
        isa = (f"ISA*00*{'':10}*00*{'':10}*ZZ*{sender:<15}*ZZ*{receiver:<15}*{date[2:]}*0800*^*00501*{control:09d}*0*P*:")
        gs = f"GS*HP*{sender}*{receiver}*{date}*0800*{control}*X*005010X221A1"
        body = self.segs
        st = [f"ST*835*0001"] + body + [f"SE*{len(body) + 2}*0001"]
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
    if method == "ACH":
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


if __name__ == "__main__":
    harborview_1110()
    harborview_1117()
    tallgrass_1112()
    problems()
    for c in CLAIMS.values():
        assert all(round(l[2] - sum(a[2] for a in l[4]), 2) >= 0 for l in c["lines"])
    print("made the samples")
