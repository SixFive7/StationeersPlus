"""
stationeers_save.py -- offline save-zip editor for Stationeers.

A Stationeers save is a plain ZIP archive containing:
    world_meta.xml     small XML, save metadata
    world.xml          ALL game state: every Thing, network ids, atmospheres, players
    terrain.dat        binary voxel data
    preview.png        thumbnail
    screenshot.png     thumbnail

This module gives agents a programmatic way to:
    1. Open a save     -> mutate XML       -> save back to a new save zip.
    2. Inspect Things  -> by ReferenceId or by PrefabName.
    3. Edit fields     -> property paths inside a Thing.
    4. Clone or insert Things.
    5. Add or remove CableNetworkId entries from the <CableNetworks> list.

Design notes (preserve forward-compat):
    - The Stationeers save loader is strict about XML shape but permissive about the
      *content* of each `<ThingSaveData>` element: every element type carries the same
      base shape (ReferenceId / PrefabName / WorldPosition / etc.) plus a type-specific
      tail (e.g. Cable carries `<CableNetworkId>`, Battery carries `<PowerStored>`, ...).
      We do not generate Thing XML from scratch in code, because the per-type
      tail is too easy to get wrong; we CLONE from an existing template Thing and
      tweak fields. The repo carries some `.work/.../Luna-extract/world.xml`
      snapshots from prior bug investigations, which are the easiest source of
      template Things.

    - ReferenceId must be unique across the entire save. `Save.next_reference_id()`
      returns max(existing) + 1.

    - Network ids (CableNetworkId, RocketNetworkId, etc.) are referenced both in the
      top-level `<CableNetworks>` list and on each Thing that belongs to that network.
      Editing a Thing's CableNetworkId without keeping the top-level list in sync
      will load OK in some cases and corrupt power state in others; prefer
      `Save.add_cable_network(id)` to keep both consistent.

    - The CLI exposes the most common operations. The Save class is also intended
      to be importable so an agent can compose more elaborate edits.

Usage:
    python stationeers_save.py extract <save.zip> <out_dir>
    python stationeers_save.py repack  <in_dir> <save.zip>
    python stationeers_save.py list    <save.zip> [--prefab PrefabName] [--type CableSaveSaveData] [--limit N]
    python stationeers_save.py show    <save.zip> --ref <ReferenceId>
    python stationeers_save.py set     <save.zip> <out.zip> --ref <ReferenceId> --field Path/To/Field --value V
    python stationeers_save.py clone   <save.zip> <out.zip> --ref <template-ref> --pos X,Y,Z [--rot QX,QY,QZ,QW]
    python stationeers_save.py add-network <save.zip> <out.zip> --id <NetworkId>
    python stationeers_save.py drop-network <save.zip> <out.zip> --id <NetworkId>

The CLI is intentionally minimal. For complex edits, import this module and
script Save() operations directly.

This tool only reads or writes save zips; it never talks to the game. It does NOT
clean up the original save -- it always writes to a new file specified on the
command line.
"""

from __future__ import annotations

import argparse
import os
import shutil
import sys
import tempfile
import zipfile
from dataclasses import dataclass
from typing import Iterable, Iterator, List, Optional
from xml.etree import ElementTree as ET


SAVE_MEMBERS = ("world_meta.xml", "world.xml", "terrain.dat", "preview.png", "screenshot.png")


@dataclass
class Thing:
    """A wrapper around one <ThingSaveData> element."""

    element: ET.Element

    @property
    def reference_id(self) -> int:
        v = self.element.findtext("ReferenceId")
        return int(v) if v is not None else 0

    @property
    def prefab_name(self) -> str:
        return self.element.findtext("PrefabName") or ""

    @property
    def xsi_type(self) -> str:
        # xsi-namespaced attribute
        for k, v in self.element.attrib.items():
            if k.endswith("}type") or k == "type":
                return v
        return ""

    def get(self, path: str) -> Optional[str]:
        node = self.element.find(path)
        return node.text if node is not None else None

    def set(self, path: str, value: str) -> None:
        """Set an existing text node by path. Raises if the path does not exist."""
        node = self.element.find(path)
        if node is None:
            raise KeyError(f"path '{path}' not found on Thing ref={self.reference_id}")
        node.text = value


class Save:
    """A Stationeers save (a ZIP) opened for editing in a temp dir."""

    def __init__(self, tempdir: str):
        self.tempdir = tempdir
        self.world_xml_path = os.path.join(tempdir, "world.xml")
        self._tree: Optional[ET.ElementTree] = None

    @classmethod
    def open(cls, zip_path: str) -> "Save":
        tmp = tempfile.mkdtemp(prefix="stationeers_save_")
        with zipfile.ZipFile(zip_path, "r") as zf:
            zf.extractall(tmp)
        return cls(tmp)

    @classmethod
    def from_extracted(cls, extracted_dir: str) -> "Save":
        return cls(extracted_dir)

    def close(self) -> None:
        if self.tempdir and os.path.isdir(self.tempdir) and self.tempdir.startswith(tempfile.gettempdir()):
            shutil.rmtree(self.tempdir, ignore_errors=True)

    def repack(self, out_zip: str) -> None:
        """Write the temp dir back as a save ZIP at out_zip."""
        if self._tree is not None:
            self._tree.write(self.world_xml_path, encoding="utf-8", xml_declaration=True)
        if os.path.exists(out_zip):
            os.remove(out_zip)
        with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zf:
            for member in SAVE_MEMBERS:
                p = os.path.join(self.tempdir, member)
                if os.path.exists(p):
                    zf.write(p, arcname=member)

    @property
    def tree(self) -> ET.ElementTree:
        if self._tree is None:
            self._tree = ET.parse(self.world_xml_path)
        return self._tree

    @property
    def root(self) -> ET.Element:
        return self.tree.getroot()

    def things(self, prefab: Optional[str] = None, xsi_type: Optional[str] = None) -> Iterator[Thing]:
        all_things = self.root.find("AllThings")
        if all_things is None:
            return
        for el in all_things.findall("ThingSaveData"):
            t = Thing(el)
            if prefab and t.prefab_name != prefab:
                continue
            if xsi_type and t.xsi_type != xsi_type:
                continue
            yield t

    def find(self, reference_id: int) -> Optional[Thing]:
        for t in self.things():
            if t.reference_id == reference_id:
                return t
        return None

    def next_reference_id(self) -> int:
        m = 0
        for t in self.things():
            if t.reference_id > m:
                m = t.reference_id
        return m + 1

    def cable_network_ids(self) -> List[int]:
        cns = self.root.find("CableNetworks")
        if cns is None:
            return []
        return [int(e.text) for e in cns.findall("NetworkId") if e.text]

    def add_cable_network(self, network_id: int) -> None:
        cns = self.root.find("CableNetworks")
        if cns is None:
            cns = ET.SubElement(self.root, "CableNetworks")
        existing = {int(e.text) for e in cns.findall("NetworkId") if e.text}
        if network_id in existing:
            return
        ET.SubElement(cns, "NetworkId").text = str(network_id)

    def drop_cable_network(self, network_id: int) -> None:
        cns = self.root.find("CableNetworks")
        if cns is None:
            return
        for e in list(cns.findall("NetworkId")):
            if e.text and int(e.text) == network_id:
                cns.remove(e)

    def clone_thing(
        self,
        template_ref: int,
        new_position: tuple,
        new_rotation: Optional[tuple] = None,
        new_reference_id: Optional[int] = None,
    ) -> Thing:
        """Deep-clone a Thing and reposition. Returns the new Thing.

        The clone keeps every field of the source EXCEPT ReferenceId, WorldPosition,
        and (if provided) WorldRotation. Network references are NOT auto-rewired:
        if you clone a Cable, the new cable still references the source cable's
        CableNetworkId. Edit that field yourself or call add_cable_network()
        for a fresh network.
        """
        src = self.find(template_ref)
        if src is None:
            raise KeyError(f"template Thing ref={template_ref} not found")

        all_things = self.root.find("AllThings")
        if all_things is None:
            raise RuntimeError("no <AllThings> element in world.xml")

        # ET deep-copy via tostring/fromstring; preserves namespaces.
        new_el = ET.fromstring(ET.tostring(src.element))
        all_things.append(new_el)
        new = Thing(new_el)

        new.set("ReferenceId", str(new_reference_id if new_reference_id is not None else self.next_reference_id()))

        wp = new_el.find("WorldPosition")
        if wp is not None:
            wp.find("x").text = str(new_position[0])
            wp.find("y").text = str(new_position[1])
            wp.find("z").text = str(new_position[2])

        rwp = new_el.find("RegisteredWorldPosition")
        if rwp is not None:
            rwp.find("x").text = str(new_position[0])
            rwp.find("y").text = str(new_position[1])
            rwp.find("z").text = str(new_position[2])

        if new_rotation:
            qx, qy, qz, qw = new_rotation
            wr = new_el.find("WorldRotation")
            if wr is not None:
                wr.find("x").text = str(qx)
                wr.find("y").text = str(qy)
                wr.find("z").text = str(qz)
                wr.find("w").text = str(qw)
            rwr = new_el.find("RegisteredWorldRotation")
            if rwr is not None:
                rwr.find("x").text = str(qx)
                rwr.find("y").text = str(qy)
                rwr.find("z").text = str(qz)
                rwr.find("w").text = str(qw)

        return new

    def remove_thing(self, reference_id: int) -> bool:
        all_things = self.root.find("AllThings")
        if all_things is None:
            return False
        for el in list(all_things.findall("ThingSaveData")):
            ref = el.findtext("ReferenceId")
            if ref and int(ref) == reference_id:
                all_things.remove(el)
                return True
        return False


# ---- CLI ----

def _cmd_extract(args):
    s = Save.open(args.zip)
    if os.path.exists(args.outdir):
        if not args.force:
            print(f"refuse to overwrite existing dir {args.outdir} (pass --force)", file=sys.stderr)
            sys.exit(2)
        shutil.rmtree(args.outdir)
    shutil.move(s.tempdir, args.outdir)
    print(f"extracted to {args.outdir}")


def _cmd_repack(args):
    s = Save.from_extracted(args.indir)
    s.repack(args.zip)
    print(f"wrote {args.zip}")


def _cmd_list(args):
    s = Save.open(args.zip)
    try:
        n = 0
        for t in s.things(prefab=args.prefab, xsi_type=args.type):
            print(f"{t.reference_id:>10}  {t.xsi_type:<32}  {t.prefab_name}")
            n += 1
            if args.limit and n >= args.limit:
                break
        print(f"({n} things)")
    finally:
        s.close()


def _cmd_show(args):
    s = Save.open(args.zip)
    try:
        t = s.find(args.ref)
        if t is None:
            print(f"no Thing with ReferenceId={args.ref}", file=sys.stderr)
            sys.exit(1)
        print(ET.tostring(t.element, encoding="unicode"))
    finally:
        s.close()


def _cmd_set(args):
    s = Save.open(args.zip)
    try:
        t = s.find(args.ref)
        if t is None:
            print(f"no Thing with ReferenceId={args.ref}", file=sys.stderr)
            sys.exit(1)
        t.set(args.field, args.value)
        s.repack(args.out)
        print(f"updated ref={args.ref} {args.field}={args.value}; wrote {args.out}")
    finally:
        s.close()


def _cmd_clone(args):
    s = Save.open(args.zip)
    try:
        pos = tuple(float(x) for x in args.pos.split(","))
        rot = tuple(float(x) for x in args.rot.split(",")) if args.rot else None
        new_thing = s.clone_thing(args.ref, pos, rot)
        s.repack(args.out)
        print(f"cloned ref={args.ref} -> ref={new_thing.reference_id} at {pos}; wrote {args.out}")
    finally:
        s.close()


def _cmd_add_network(args):
    s = Save.open(args.zip)
    try:
        s.add_cable_network(args.id)
        s.repack(args.out)
        print(f"added CableNetwork id={args.id}; wrote {args.out}")
    finally:
        s.close()


def _cmd_drop_network(args):
    s = Save.open(args.zip)
    try:
        s.drop_cable_network(args.id)
        s.repack(args.out)
        print(f"dropped CableNetwork id={args.id}; wrote {args.out}")
    finally:
        s.close()


def main():
    p = argparse.ArgumentParser(description="Stationeers save-zip editor")
    sub = p.add_subparsers(dest="cmd", required=True)

    e = sub.add_parser("extract", help="extract a save ZIP to a directory")
    e.add_argument("zip")
    e.add_argument("outdir")
    e.add_argument("--force", action="store_true")
    e.set_defaults(func=_cmd_extract)

    r = sub.add_parser("repack", help="repack an extracted directory into a save ZIP")
    r.add_argument("indir")
    r.add_argument("zip")
    r.set_defaults(func=_cmd_repack)

    l = sub.add_parser("list", help="list Things in a save")
    l.add_argument("zip")
    l.add_argument("--prefab")
    l.add_argument("--type")
    l.add_argument("--limit", type=int, default=0)
    l.set_defaults(func=_cmd_list)

    sh = sub.add_parser("show", help="dump one Thing's XML to stdout")
    sh.add_argument("zip")
    sh.add_argument("--ref", type=int, required=True)
    sh.set_defaults(func=_cmd_show)

    st = sub.add_parser("set", help="set a field on a Thing and write the save")
    st.add_argument("zip")
    st.add_argument("out")
    st.add_argument("--ref", type=int, required=True)
    st.add_argument("--field", required=True, help="XPath inside the Thing, e.g. OnOff or DamageState/Burn")
    st.add_argument("--value", required=True)
    st.set_defaults(func=_cmd_set)

    cl = sub.add_parser("clone", help="clone a Thing to a new world position")
    cl.add_argument("zip")
    cl.add_argument("out")
    cl.add_argument("--ref", type=int, required=True)
    cl.add_argument("--pos", required=True, help="X,Y,Z")
    cl.add_argument("--rot", help="QX,QY,QZ,QW (optional)")
    cl.set_defaults(func=_cmd_clone)

    an = sub.add_parser("add-network", help="add a CableNetworkId to the top-level list")
    an.add_argument("zip")
    an.add_argument("out")
    an.add_argument("--id", type=int, required=True)
    an.set_defaults(func=_cmd_add_network)

    dn = sub.add_parser("drop-network", help="drop a CableNetworkId from the top-level list")
    dn.add_argument("zip")
    dn.add_argument("out")
    dn.add_argument("--id", type=int, required=True)
    dn.set_defaults(func=_cmd_drop_network)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
