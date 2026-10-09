"""text_probe.py - list readable text found in a DVD's files.

    python text_probe.py <VIDEO_TS folder> [--all] [--min N] [--out FILE]

Scans every .IFO and every menu VOB (VIDEO_TS.VOB, VTS_nn_0.VOB) in the
folder for runs of readable text and prints each with its file and byte
offset. --all also scans the title VOBs (VTS_nn_1.VOB and up), which are
several GB and take a while. --min sets the shortest run reported (default 8).

Compressed video is full of short accidental runs of printable bytes, so a
run is only reported when it reads like words: at least 3 letters, and
letters and spaces making up at least 70% of it.

The runs are written to text_scan.txt in the current folder (--out to
choose another file); the screen shows a count per file as it goes. The
DVD's files are only read, never changed.
"""

import argparse
import os
import re
import sys

CHUNK = 4 * 1024 * 1024
PRINTABLE = re.compile(rb"[\x20-\x7e]+")
MENU_VOB = re.compile(r"^(VIDEO_TS|VTS_\d\d_0)\.VOB$", re.IGNORECASE)
TITLE_VOB = re.compile(r"^VTS_\d\d_[1-9]\.VOB$", re.IGNORECASE)


def wordlike(text):
    letters = sum(c.isalpha() for c in text)
    spaces = text.count(" ")
    return letters >= 3 and (letters + spaces) / len(text) >= 0.7


def scan(path, minimum):
    """Yields (offset, text) for each word-like run in the file."""
    with open(path, "rb") as f:
        base = 0
        carry = b""
        while True:
            block = f.read(CHUNK)
            data = carry + block
            if not data:
                return
            # A run touching the end of the block may continue in the next
            # one, so it is held back and scanned with that block.
            last = len(data) if not block else len(data) - len(re.search(rb"[\x20-\x7e]*$", data).group())
            for m in PRINTABLE.finditer(data, 0, last):
                text = m.group().decode("ascii")
                if len(text) >= minimum and wordlike(text):
                    yield base + m.start(), text
            carry = data[last:]
            base += last
            if not block:
                return


def main(argv):
    parser = argparse.ArgumentParser(description="List readable text found in a DVD's files.")
    parser.add_argument("folder", help="the VIDEO_TS folder")
    parser.add_argument("--all", action="store_true", help="also scan the title VOBs")
    parser.add_argument("--min", type=int, default=8, help="shortest run reported (default 8)")
    parser.add_argument("--out", default="text_scan.txt", help="file to write (default text_scan.txt)")
    args = parser.parse_args(argv[1:])
    if not os.path.isdir(args.folder):
        parser.error(f"not a folder: {args.folder}")

    names = sorted(n for n in os.listdir(args.folder)
                   if n.upper().endswith(".IFO") or MENU_VOB.match(n)
                   or (args.all and TITLE_VOB.match(n)))

    if not names:
        parser.error(f"no IFO or VOB files in {args.folder}")

    with open(args.out, "w", encoding="utf-8") as out:
        for name in names:
            path = os.path.join(args.folder, name)
            found = 0
            print(f"scanning {name} ({os.path.getsize(path):,} bytes)", end="", flush=True)
            out.write(f"== {name} ({os.path.getsize(path):,} bytes)\n")
            for offset, text in scan(path, args.min):
                out.write(f"  {offset:>12,}  {text}\n")
                found += 1
            out.write(f"   {found} run(s)\n")
            print(f": {found} run(s)")
    print(f"written to {os.path.abspath(args.out)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
