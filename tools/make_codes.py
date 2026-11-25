#!/usr/bin/env python3
"""Makes src/AVARemittance.Core/Codes/*.tsv from x12.org's published code lists.

    python3 tools/make_codes.py            # downloads the four pages
    python3 tools/make_codes.py FOLDER     # reads carc.html, rarc.html, status-category.html and status.html saved in FOLDER

Each line of a .tsv is: code, start (yyyy-mm-dd), stop (yyyy-mm-dd or empty), description. The description is the
list's own wording with the usage notes left off (from " Usage:", " Note:" or "At least one Remark Code must be provided" on), as the program shows it.
"""
import html, os, re, sys, urllib.request

PAGES = {
    "carc": "claim-adjustment-reason-codes",
    "rarc": "remittance-advice-remark-codes",
    "status-category": "claim-status-category-codes",
    "status": "claim-status-codes",
}
OUT = os.path.join(os.path.dirname(__file__), "..", "src", "AVARemittance.Core", "Codes")


def page(folder, key):
    if folder:
        return open(os.path.join(folder, key + ".html"), encoding="utf-8").read()
    req = urllib.request.Request("https://x12.org/codes/" + PAGES[key], headers={"User-Agent": "Mozilla/5.0"})
    return urllib.request.urlopen(req).read().decode("utf-8")


def date(text):
    m = re.match(r"(\d\d)/(\d\d)/(\d{4})", text.strip())
    return f"{m[3]}-{m[1]}-{m[2]}" if m else ""


def rows(text):
    for code, desc in re.findall(r'<td class="code">(.*?)</td><td class="description">(.*?)</td>', text, re.S):
        dates = re.search(r'<span class="dates">(.*?)</span>', desc, re.S)
        words = re.sub(r'<span class="dates">.*?</span>', "", desc, flags=re.S)
        words = html.unescape(re.sub(r"<[^>]+>", " ", words))
        words = re.sub(r"\s+", " ", words).strip()
        words = re.split(r" (?:Usage|Note|Notes): | At least one Remark Code must be provided", words)[0].strip()
        start = stop = ""
        if dates:
            d = html.unescape(re.sub(r"<[^>]+>", " ", dates[1]))
            if m := re.search(r"Start:\s*([\d/]+)", d): start = date(m[1])
            if m := re.search(r"Stop:\s*([\d/]+)", d): stop = date(m[1])
        yield html.unescape(code).strip(), start, stop, words


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else None
    os.makedirs(OUT, exist_ok=True)
    for key in PAGES:
        seen = {}
        for code, start, stop, words in rows(page(folder, key)):
            seen.setdefault(code, (start, stop, words))
        with open(os.path.join(OUT, key + ".tsv"), "w", encoding="utf-8", newline="\n") as f:
            for code, (start, stop, words) in seen.items():
                f.write(f"{code}\t{start}\t{stop}\t{words}\n")
        print(key, len(seen))


if __name__ == "__main__":
    main()
