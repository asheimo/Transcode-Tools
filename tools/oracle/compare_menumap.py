"""compare_menumap.py - check the app's disc.json against the oracle's menumap.json.

    python compare_menumap.py <menumap.json> <disc.json>

Compares, for the same disc:
  - titles: number, VTS, VTS title, angles, chapters
  - screens: the same list, matched by IFO + language + PGC + cell, with the
    same first/last sectors
  - per screen: NAV pack count, and every button set: button number,
    rectangle, arrow-key neighbours, raw command bytes, command text
  - per button: where the resolver says it leads (kind, title, chapter,
    menu, why), with the full trace and notes
  - per screen: whether it got a picture, and the picture's file name
  - per title: which buttons name it (IFO, PGC, button) and which PGC
    commands jump to it

The menumap.json being compared against was made by the old Python run,
which only read the first 8,000 sectors of each cell. The app reads every
cell in full, so for a cell longer than that the app's NAV pack count is
higher. There the oracle's button sets must be the first sets the app found;
anything extra the app found is printed, not failed. The note is only
printed when something extra turns up.

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


RESOLVED_KEYS = [  # oracle key, app key
    ("kind", "Kind"), ("title", "Title"), ("chapter", "Chapter"), ("vts", "Vts"),
    ("vts_title", "VtsTitle"), ("chapters", "Chapters"), ("menu_pgc", "MenuPgc"),
    ("domain", "Domain"), ("button", "Button"), ("why", "Why"),
    ("trace", "Trace"), ("notes", "Notes"),
]


def oracle_resolved(b):
    r = b.get("resolved") or {}
    return {k: r.get(k) for k, _ in RESOLVED_KEYS if r.get(k) is not None}


def app_resolved(b):
    r = b.get("Resolved") or {}
    return {k: r.get(a) for k, a in RESOLVED_KEYS if r.get(a) is not None}


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

    checked = capped = sets_total = buttons_total = resolved_total = pictures = 0
    notes = []

    for key in sorted(set(o_screens) & set(a_screens)):
        o, a = o_screens[key], a_screens[key]
        checked += 1
        first, last = o["sectors"]
        if o.get("image") != a.get("Image"):
            diffs.append(f"{name(key)}: picture oracle {o.get('image')}, app {a.get('Image')}")
        if o.get("image"):
            pictures += 1
        if (first, last) != (a["FirstSector"], a["LastSector"]):
            diffs.append(f"{name(key)}: sectors oracle {first}-{last}, "
                         f"app {a['FirstSector']}-{a['LastSector']}")

        o_sets = [[oracle_button(b) for b in bs["buttons"]] for bs in o["button_sets"]]
        a_sets = [[app_button(b) for b in bs["Buttons"]] for bs in a["ButtonSets"]]

        # where each button leads, for the sets both sides have
        for si, (obs, abs_) in enumerate(zip(o["button_sets"], a["ButtonSets"]), start=1):
            for ob, ab in zip(obs["buttons"], abs_["Buttons"]):
                resolved_total += 1
                orr, arr = oracle_resolved(ob), app_resolved(ab)
                if orr != arr:
                    diffs.append(f"{name(key)} set {si} button {ob['button']} resolves differently:")
                    diffs.append(f"    oracle {json.dumps(orr)}")
                    diffs.append(f"    app    {json.dumps(arr)}")
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
            if extra > 0:
                notes.append(f"{name(key)}: the app found {extra} button set(s) the old "
                             f"Python run missed, because that run only read this cell's "
                             f"first {ORACLE_CAP} sectors:")
            for i, s in enumerate(a_sets[len(o_sets):], start=len(o_sets) + 1):
                notes.append(f"    extra set {i}:")
                for b in s:
                    notes.append(f"      {fmt(b)}")
            continue

        if a["NavPacks"] != o["nav_packs"]:
            diffs.append(f"{name(key)}: NAV packs oracle {o['nav_packs']}, app {a['NavPacks']}")
        if o_sets != a_sets:
            diffs.extend(set_diffs(key, o_sets, a_sets))

    # -- title coverage -------------------------------------------------------
    a_by_number = {t["Number"]: t for t in app["Titles"]}
    covered = 0
    for c in oracle.get("coverage") or []:
        t = a_by_number.get(c["title"])
        if t is None:
            continue
        covered += 1
        o_named = [(n["ifo"], n["pgc"], n["button"]) for n in c["named_by"]]
        a_named = [(n["Ifo"], n["Pgc"], n["Button"]) for n in t.get("NamedBy") or []]
        if o_named != a_named:
            diffs.append(f"title {c['title']} named by: oracle {o_named}, app {a_named}")
        if list(c["reached_from"]) != list(t.get("ReachedFrom") or []):
            diffs.append(f"title {c['title']} reached from: oracle {c['reached_from']}, "
                         f"app {t.get('ReachedFrom')}")

    # -- report ---------------------------------------------------------------
    print(f"titles: oracle {len(o_titles)}, app {len(a_titles)}")
    print(f"screens compared: {checked} (oracle {len(o_screens)}, app {len(a_screens)})")
    print(f"oracle button sets checked: {sets_total}, buttons: {buttons_total}, "
          f"resolved targets: {resolved_total}")
    print(f"screens with a picture in the oracle: {pictures}; titles with coverage checked: {covered}")
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
