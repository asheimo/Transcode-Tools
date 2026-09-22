"""compare_menumap.py - check the app's disc.json against the oracle's menumap.json.

    python compare_menumap.py <menumap.json> <disc.json>

Compares, for the same disc:
  - titles: number, VTS, VTS title, angles, chapters
  - screens: the same list, matched by IFO + language + PGC + cell, with the
    same first/last sectors
  - per screen: NAV pack count, and every button set: button number,
    rectangle, arrow-key neighbours, raw command bytes, command text

The oracle stopped scanning a cell after 8,000 sectors; the app scans every
cell in full. For a cell longer than that, the oracle's button sets must be
the first sets the app found, and the app's NAV pack count must be at least
the oracle's; anything the app found beyond them is listed, not failed.

Prints one line per difference and a summary. Exit code 0 when everything
matches, 1 otherwise.
"""

import json
import sys

ORACLE_CAP = 8000


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return json.load(f)


def oracle_button(b):
    return (b["button"], tuple(b["rect"]), b["up"], b["down"], b["left"], b["right"],
            b["raw"], b["command"])


def app_button(b):
    return (b["Number"], (b["X0"], b["Y0"], b["X1"], b["Y1"]), b["Up"], b["Down"],
            b["Left"], b["Right"], b["Raw"], b["CommandText"])


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2

    oracle, app = load(argv[1]), load(argv[2])
    diffs = []

    # -- titles ---------------------------------------------------------------
    o_titles = [(t["title"], t["vts"], t["vts_title"], t["angles"], t["chapters"])
                for t in oracle["titles"]]
    a_titles = [(t["Number"], t["Vts"], t["VtsTitle"], t["Angles"], t["Chapters"])
                for t in app["Titles"]]
    if o_titles != a_titles:
        diffs.append(f"titles differ:\n    oracle {o_titles}\n    app    {a_titles}")

    # -- screens --------------------------------------------------------------
    o_screens = {}
    for m in oracle["menus"]:
        for s in m["screens"]:
            key = (m["ifo"], m["lang"], m["pgc"], s["cell"])
            o_screens[key] = s
    a_screens = {(s["Ifo"], s["Lang"], s["Pgc"], s["Cell"]): s for s in app["Screens"]}

    for key in sorted(set(o_screens) - set(a_screens)):
        diffs.append(f"{name(key)}: in the oracle, missing from the app")
    for key in sorted(set(a_screens) - set(o_screens)):
        diffs.append(f"{name(key)}: in the app, not in the oracle")

    checked = capped = sets_total = buttons_total = 0
    notes = []

    for key in sorted(set(o_screens) & set(a_screens)):
        o, a = o_screens[key], a_screens[key]
        checked += 1
        first, last = o["sectors"]
        if (first, last) != (a["FirstSector"], a["LastSector"]):
            diffs.append(f"{name(key)}: sectors oracle {first}-{last}, "
                         f"app {a['FirstSector']}-{a['LastSector']}")

        o_sets = [[oracle_button(b) for b in bs["buttons"]] for bs in o["button_sets"]]
        a_sets = [[app_button(b) for b in bs["Buttons"]] for bs in a["ButtonSets"]]
        sets_total += len(o_sets)
        buttons_total += sum(len(s) for s in o_sets)

        if last - first + 1 > ORACLE_CAP:
            capped += 1
            if a["NavPacks"] < o["nav_packs"]:
                diffs.append(f"{name(key)}: NAV packs app {a['NavPacks']} < oracle "
                             f"{o['nav_packs']} (oracle only read {ORACLE_CAP} sectors)")
            if a_sets[:len(o_sets)] != o_sets:
                diffs.append(f"{name(key)}: the oracle's {len(o_sets)} button set(s) are not "
                             f"the first sets the app found")
                diffs.extend(set_diffs(key, o_sets, a_sets[:len(o_sets)]))
            extra = len(a_sets) - len(o_sets)
            notes.append(f"{name(key)}: {last - first + 1} sectors, longer than the oracle's "
                         f"{ORACLE_CAP}-sector limit. NAV packs oracle {o['nav_packs']}, app "
                         f"{a['NavPacks']}. Button sets past the old limit: "
                         f"{extra if extra > 0 else 'none'}.")
            for i, s in enumerate(a_sets[len(o_sets):], start=len(o_sets) + 1):
                notes.append(f"    extra set {i}:")
                for b in s:
                    notes.append(f"      {fmt(b)}")
            continue

        if a["NavPacks"] != o["nav_packs"]:
            diffs.append(f"{name(key)}: NAV packs oracle {o['nav_packs']}, app {a['NavPacks']}")
        if o_sets != a_sets:
            diffs.extend(set_diffs(key, o_sets, a_sets))

    # -- report ---------------------------------------------------------------
    print(f"titles: oracle {len(o_titles)}, app {len(a_titles)}")
    print(f"screens compared: {checked} (oracle {len(o_screens)}, app {len(a_screens)})")
    print(f"oracle button sets checked: {sets_total}, buttons: {buttons_total}")
    if capped:
        print(f"screens past the oracle's {ORACLE_CAP}-sector limit: {capped}")
        for line in notes:
            print(line)
    print()
    if diffs:
        print(f"DIFFERENCES: {len(diffs)}")
        for d in diffs:
            print("  " + d)
        return 1
    print("MATCH: the app agrees with the oracle on every compared field.")
    return 0


def set_diffs(key, o_sets, a_sets):
    out = []
    if len(o_sets) != len(a_sets):
        out.append(f"{name(key)}: button sets oracle {len(o_sets)}, app {len(a_sets)}")
    for i, (os_, as_) in enumerate(zip(o_sets, a_sets), start=1):
        if os_ == as_:
            continue
        out.append(f"{name(key)} set {i}:")
        for ob, ab in zip(os_, as_):
            if ob != ab:
                out.append(f"    oracle {fmt(ob)}")
                out.append(f"    app    {fmt(ab)}")
        if len(os_) != len(as_):
            out.append(f"    buttons oracle {len(os_)}, app {len(as_)}")
    return out


def name(key):
    ifo, lang, pgc, cell = key
    return f"{ifo} {lang} pgc {pgc} cell {cell}"


def fmt(b):
    num, rect, up, down, left, right, raw, text = b
    return f"button {num} {list(rect)} u{up} d{down} l{left} r{right} {raw} {text}"


if __name__ == "__main__":
    sys.exit(main(sys.argv))
