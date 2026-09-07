#!/usr/bin/env python3
"""profile-code.py -- build the NEWWORLD r2modman profile and turn it into an import code.

Stdlib only. Lives on the box at /opt/modlab/tools/cutover/profile-code.py.

What it does
  1. resolves every mod's LATEST version from Thunderstore's experimental package API
     (https://thunderstore.io/api/experimental/package/<ns>/<name>/ -> latest.version_number)
  2. writes an r2modman profile export (.r2z = a zip with `export.r2x`, YAML, at the root)
     in exactly the format r2modman itself writes (ExportFormat/ExportMod: profileName + mods[]
     with name / version{major,minor,patch} / enabled)
  3. uploads it to Thunderstore's profile-sharing endpoint and prints the import code

Verified against ebkr/r2modmanPlus (src/r2mm/profiles/ProfilesClient.ts, src/r2mm/mods/
ProfileModList.ts) and thunderstore-io/Thunderstore (modpacks/api/experimental):
  POST https://thunderstore.io/api/experimental/legacyprofile/create/
  Content-Type: application/octet-stream
  body: the literal text  "#r2modman\\n" + base64(zip bytes)
  response: {"key": "<uuid>"}          (throttled to 6 creates/minute; 20 MB client-side cap)
  read back with GET .../legacyprofile/get/<key>/  (302 -> the stored file)
Code expiry: NONE in the Thunderstore source -- the LegacyProfile model has no TTL and there is
no cleanup job. Treat codes as long-lived but not contractually permanent (an admin can delete a
row). Identical uploads are de-duplicated by sha256 and return the SAME key.

Usage:
  profile-code.py                       # resolve, build, upload, print the code
  profile-code.py --dry-run             # resolve + build + print export.r2x, do NOT upload
  profile-code.py --name NEWWORLD --out /tmp/NEWWORLD.r2z
  profile-code.py --pin Nosferatu-SmoothServer=0.4.0   # repeatable; pin instead of resolving
"""

import argparse
import base64
import io
import json
import os
import sys
import urllib.error
import urllib.request
import zipfile

TS_PKG = "https://thunderstore.io/api/experimental/package/%s/%s/"
TS_CREATE = "https://thunderstore.io/api/experimental/legacyprofile/create/"
TS_GET = "https://thunderstore.io/api/experimental/legacyprofile/get/%s/"
UA = "orion-valheim-cutover/1.0 (+https://github.com/MJensen01/SmoothServer)"

# The NEWWORLD profile, exactly. Order = the order r2modman shows them in.
MODS = [
    "denikson-BepInExPack_Valheim",
    "ValheimModding-Jotunn",
    "LEGIOmods-TraderBrothers",
    "blacks7ar-CookingAdditions",
    "HugotheDwarf-Shapekeys_and_More",
    "HugotheDwarf-More_and_Modified_Player_Cloth_Colliders",
    "HugotheDwarf-Hugos_Armory",
    "Azumatt-Official_BepInEx_ConfigurationManager",
    "Nosferatu-NoVikingLeftBehind",
    "Nosferatu-SmoothServer",
]


def get_json(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))


def latest_version(full_name):
    ns, name = full_name.split("-", 1)
    data = get_json(TS_PKG % (ns, name))
    v = data.get("latest", {}).get("version_number")
    if not v:
        raise RuntimeError("no latest.version_number for %s" % full_name)
    return v


def r2x(profile_name, resolved):
    """Reproduce js-yaml's dump() of r2modman's ExportFormat exactly."""
    out = ["profileName: %s" % profile_name, "mods:"]
    for full_name, ver in resolved:
        major, minor, patch = (ver.split(".") + ["0", "0", "0"])[:3]
        out += ["  - name: %s" % full_name,
                "    version:",
                "      major: %d" % int(major),
                "      minor: %d" % int(minor),
                "      patch: %d" % int(patch),
                "    enabled: true"]
    return "\n".join(out) + "\n"


def build_zip(text):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("export.r2x", text)
    return buf.getvalue()


def upload(zip_bytes):
    body = ("#r2modman\n" + base64.b64encode(zip_bytes).decode("ascii")).encode("utf-8")
    req = urllib.request.Request(
        TS_CREATE, data=body, method="POST",
        headers={"Content-Type": "application/octet-stream", "User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))["key"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="NEWWORLD", help="profile name (default NEWWORLD)")
    ap.add_argument("--out", default=None, help="also write the .r2z here")
    ap.add_argument("--dry-run", action="store_true", help="build but do not upload")
    ap.add_argument("--pin", action="append", default=[], metavar="Ns-Name=X.Y.Z",
                    help="pin one mod instead of resolving its latest version")
    a = ap.parse_args()

    pins = dict(p.split("=", 1) for p in a.pin)

    print("== resolving versions from Thunderstore ==")
    resolved = []
    for full_name in MODS:
        if full_name in pins:
            ver, how = pins[full_name], "pinned"
        else:
            try:
                ver, how = latest_version(full_name), "latest"
            except Exception as e:                      # noqa: BLE001
                print("  ERROR %-58s %s" % (full_name, e), file=sys.stderr)
                return 2
        resolved.append((full_name, ver))
        print("  %-58s %-10s (%s)" % (full_name, ver, how))

    text = r2x(a.name, resolved)
    zip_bytes = build_zip(text)
    print("\n== export.r2x (%d bytes, zip %d bytes) ==" % (len(text), len(zip_bytes)))
    if a.dry_run:
        print(text)

    out = a.out or os.path.join(os.path.dirname(os.path.abspath(__file__)), "%s.r2z" % a.name)
    try:
        with open(out, "wb") as f:
            f.write(zip_bytes)
        print("wrote %s" % out)
    except OSError as e:
        print("could not write %s: %s" % (out, e), file=sys.stderr)

    if a.dry_run:
        print("\n--dry-run: not uploaded.")
        return 0

    print("\n== uploading to Thunderstore ==")
    try:
        key = upload(zip_bytes)
    except urllib.error.HTTPError as e:
        print("upload failed: HTTP %s %s\n%s" % (e.code, e.reason, e.read()[:500]), file=sys.stderr)
        return 3
    except Exception as e:                              # noqa: BLE001
        print("upload failed: %s" % e, file=sys.stderr)
        return 3

    print("""
==========================================================================
  r2modman / Thunderstore Mod Manager import code for profile "%s"

      %s

  Send that to the group. In r2modman:
    Profiles -> Select profile -> Import / Update -> Import code
    -> paste the code -> "Import as new profile" -> name it %s -> OK
    -> select it -> Start modded
  Then Steam -> Valheim -> Properties -> Betas -> None.

  Read it back / verify: %s
  Expiry: none in Thunderstore's source (no TTL field, no cleanup job) -- treat
  it as long-lived, but regenerate before a session if it has been months.
  Re-running this script with the same mod versions returns the SAME code
  (server de-duplicates by sha256).
==========================================================================""" % (
        a.name, key, a.name, TS_GET % key))
    return 0


if __name__ == "__main__":
    sys.exit(main())
