#!/usr/bin/env python3
"""Download a NuGet package closure via the flat-container API (works through
the egress proxy where dotnet's own HTTP stack times out)."""
import os, re, sys, urllib.request, zipfile, xml.etree.ElementTree as ET

FEED = "https://api.nuget.org/v3-flatcontainer"
OUT = os.path.expanduser("~/workspace/usenet-backup/.nuget-local")
os.makedirs(OUT, exist_ok=True)

def fetch(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": "curl/8"})
    with urllib.request.urlopen(req, timeout=60) as r, open(dest, "wb") as f:
        f.write(r.read())

def nupkg_url(pid, ver):
    p = pid.lower()
    return f"{FEED}/{p}/{ver.lower()}/{p}.{ver.lower()}.nupkg"

def min_version(spec):
    # specs like "2.9.2", "[2.1.10, )", "(, 3.0.0]", "[1.0.0]"
    m = re.search(r"(\d+\.\d+\.\d+(?:\.\d+)?)", spec or "")
    return m.group(1) if m else None

def deps_of(nupkg_path):
    deps = []
    with zipfile.ZipFile(nupkg_path) as z:
        nuspec = next(n for n in z.namelist() if n.endswith(".nuspec"))
        root = ET.fromstring(z.read(nuspec))
    ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
    for d in root.findall(".//n:dependency", ns):
        v = min_version(d.get("version"))
        if v:
            deps.append((d.get("id"), v))
    # also try no-namespace (older nuspecs)
    if not deps:
        for d in root.findall(".//dependency"):
            v = min_version(d.get("version"))
            if v:
                deps.append((d.get("id"), v))
    return deps

queue = [tuple(s.split("==")) for s in sys.argv[1:]]
seen = {}
while queue:
    pid, ver = queue.pop(0)
    key = pid.lower()
    if key in seen:
        continue
    seen[key] = ver
    dest = os.path.join(OUT, f"{pid}.{ver}.nupkg")
    if not os.path.exists(dest):
        print(f"downloading {pid} {ver} ...", flush=True)
        try:
            fetch(nupkg_url(pid, ver), dest)
        except Exception as e:
            print(f"FAILED {pid} {ver}: {nupkg_url(pid, ver)} -> {e}", flush=True)
            continue
    else:
        print(f"cached {pid} {ver}", flush=True)
    for did, dver in deps_of(dest):
        if did.lower() not in seen:
            queue.append((did, dver))

print(f"\nDone: {len(seen)} packages in {OUT}")
