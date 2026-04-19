#!/usr/bin/env python3
"""
Stationeers terrain reset tool.

Resets all terrain modifications in a .save file except those inside
sealed rooms (and a configurable margin for walls/frames).

Usage:
    python terrain_reset.py path/to/save.save
    python terrain_reset.py path/to/save.save --margin 3
    python terrain_reset.py path/to/save.save --dry-run
"""

import argparse
import struct
import zipfile
import shutil
import re
import io
import sys
from pathlib import Path


IS_LEAF = 0x01
IS_MODIFIED = 0x02


def parse_rooms(world_xml: str) -> list[tuple[int, int, int]]:
    """Extract room grid cells from world.xml, converted to world coordinates.

    Each grid cell is stored at 10x scale in the XML. Dividing by 10 gives
    world-space integer coordinates. Each cell represents a 2x2x2 voxel block.
    """
    rooms_match = re.search(r"<Rooms>(.*?)</Rooms>", world_xml, re.DOTALL)
    if not rooms_match:
        return []
    grid_pattern = re.compile(
        r"<Grid>\s*<x>(-?\d+)</x>\s*<y>(-?\d+)</y>\s*<z>(-?\d+)</z>\s*</Grid>"
    )
    cells = []
    for m in grid_pattern.finditer(rooms_match.group(1)):
        cells.append((int(m.group(1)) // 10, int(m.group(2)) // 10, int(m.group(3)) // 10))
    return cells


def build_keep_set(
    room_cells: list[tuple[int, int, int]], margin: int
) -> tuple[set[tuple[int, int, int]], tuple[int, int, int, int, int, int]]:
    """Expand room cells to voxels (2x2x2 per cell) plus a margin, return set and bounding box."""
    room_voxels = set()
    for cx, cy, cz in room_cells:
        for dx in range(2):
            for dy in range(2):
                for dz in range(2):
                    room_voxels.add((cx + dx, cy + dy, cz + dz))

    keep = set()
    for vx, vy, vz in room_voxels:
        for dx in range(-margin, margin + 1):
            for dy in range(-margin, margin + 1):
                for dz in range(-margin, margin + 1):
                    keep.add((vx + dx, vy + dy, vz + dz))

    if not keep:
        return keep, (0, 0, 0, 0, 0, 0)

    xs = [v[0] for v in keep]
    ys = [v[1] for v in keep]
    zs = [v[2] for v in keep]
    bbox = (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))
    return keep, bbox


def _bbox_overlaps(ox: int, oy: int, oz: int, size: int, bbox: tuple, origin: int) -> bool:
    """Check if octree node bounding box overlaps the keep bounding box (in world coords)."""
    wx, wy, wz = ox - origin, oy, oz - origin
    return not (
        wx + size - 1 < bbox[0]
        or wx > bbox[1]
        or wy + size - 1 < bbox[2]
        or wy > bbox[3]
        or wz + size - 1 < bbox[4]
        or wz > bbox[5]
    )


class OctreeReader:
    """Reads nodes from a terrain.dat byte buffer."""

    def __init__(self, data: bytes, start: int = 0):
        self.data = data
        self.pos = start

    def read_byte(self) -> int:
        b = self.data[self.pos]
        self.pos += 1
        return b

    def skip_node(self) -> int:
        """Skip a node subtree. Returns count of modified leaves skipped."""
        flags = self.read_byte()
        if flags & IS_LEAF:
            self.pos += 2
            return 1 if (flags & IS_MODIFIED) else 0
        count = 0
        for _ in range(8):
            count += self.skip_node()
        return count


class TerrainRewriter:
    """Rewrites a terrain.dat octree, pruning modifications outside a keep zone."""

    def __init__(self, data: bytes, keep_bbox: tuple, origin: int):
        self.reader = OctreeReader(data, start=4)
        self.max_depth = struct.unpack_from("<i", data, 0)[0]
        self.origin = origin
        self.keep_bbox = keep_bbox
        self.output = bytearray()

        self.stats = {
            "kept": 0,
            "reset": 0,
            "branches_collapsed": 0,
            "nodes_passthrough": 0,
        }

    def rewrite(self) -> bytes:
        self.output.extend(struct.pack("<i", self.max_depth))
        self._rewrite_node(0, 0, 0, 0)
        return bytes(self.output)

    @property
    def octree_end_pos(self) -> int:
        return self.reader.pos

    def _rewrite_node(self, depth: int, ox: int, oy: int, oz: int):
        r = self.reader
        flags = r.read_byte()
        is_leaf = flags & IS_LEAF
        is_modified = flags & IS_MODIFIED
        size = 1 << (self.max_depth - depth)

        overlaps = _bbox_overlaps(ox, oy, oz, size, self.keep_bbox, self.origin)

        if is_leaf:
            density = r.read_byte()
            node_type = r.read_byte()

            if is_modified and not overlaps:
                self.output.extend(b"\x01\x00\x00")
                self.stats["reset"] += 1
            else:
                self.output.extend([flags, density, node_type])
                if is_modified:
                    self.stats["kept"] += 1
                else:
                    self.stats["nodes_passthrough"] += 1
        else:
            if not overlaps:
                skipped = 0
                for _ in range(8):
                    skipped += r.skip_node()
                self.output.extend(b"\x01\x00\x00")
                self.stats["branches_collapsed"] += 1
                self.stats["reset"] += skipped
            else:
                self.output.append(flags)
                self.stats["nodes_passthrough"] += 1
                half = size >> 1
                for i in range(8):
                    cx = ox + (half if (i & 1) else 0)
                    cy = oy + (half if (i & 2) else 0)
                    cz = oz + (half if (i & 4) else 0)
                    self._rewrite_node(depth + 1, cx, cy, cz)


def rewrite_vein_data(data: bytes, vein_start: int, keep_bbox: tuple, origin: int) -> bytes:
    """Filter vein records, keeping only those inside the keep bounding box."""
    pos = vein_start
    if pos + 4 > len(data):
        return b"\x00\x00\x00\x00"

    vein_count = struct.unpack_from("<i", data, pos)[0]
    pos += 4

    kept_veins = bytearray()
    kept_count = 0

    for _ in range(vein_count):
        record_start = pos
        vwx, vwy, vwz = struct.unpack_from("<hhh", data, pos)
        pos += 6
        pos += 6  # ClusterPosition
        pos += 4  # IdHash
        minables_len = data[pos]
        pos += 1
        pos += minables_len * 5  # X, Y, Z, ParentIndex, IsActive

        if (
            keep_bbox[0] <= vwx <= keep_bbox[1]
            and keep_bbox[2] <= vwy <= keep_bbox[3]
            and keep_bbox[4] <= vwz <= keep_bbox[5]
        ):
            kept_veins.extend(data[record_start:pos])
            kept_count += 1

    result = struct.pack("<i", kept_count) + bytes(kept_veins)
    return result


def zero_checksums(world_xml: str) -> str:
    """Replace all TerrainChunkChecksums values with 0."""
    def replace_checksum(m):
        return re.sub(r"<int>-?\d+</int>", "<int>0</int>", m.group(0))

    return re.sub(
        r"<TerrainChunkChecksums>.*?</TerrainChunkChecksums>",
        replace_checksum,
        world_xml,
        flags=re.DOTALL,
    )


def process_save(save_path: str, margin: int = 3, dry_run: bool = False):
    save = Path(save_path)
    if not save.exists():
        print(f"Error: {save} not found", file=sys.stderr)
        sys.exit(1)

    print(f"Processing: {save}")
    print(f"Margin: {margin} voxels around room cells")
    print()

    # Extract
    with zipfile.ZipFile(save, "r") as zf:
        world_xml = zf.read("world.xml").decode("utf-8")
        terrain_data = zf.read("terrain.dat")
        other_files = {}
        for name in zf.namelist():
            if name not in ("world.xml", "terrain.dat"):
                other_files[name] = zf.read(name)

    # Parse rooms
    room_cells = parse_rooms(world_xml)
    if not room_cells:
        print("WARNING: No room grid cells found in world.xml.")
        print("This means either there are no sealed rooms, or the save uses")
        print("a different room format. Proceeding would reset ALL terrain.")
        resp = input("Continue anyway? [y/N] ")
        if resp.lower() != "y":
            print("Aborted.")
            return
        keep_set, keep_bbox = set(), (0, 0, 0, 0, 0, 0)
    else:
        keep_set, keep_bbox = build_keep_set(room_cells, margin)

    max_depth = struct.unpack_from("<i", terrain_data, 0)[0]
    origin = (1 << max_depth) // 2

    print(f"Room cells: {len(room_cells)}")
    print(f"Keep set: {len(keep_set)} voxels")
    print(f"Keep bounding box (world): X[{keep_bbox[0]},{keep_bbox[1]}] "
          f"Y[{keep_bbox[2]},{keep_bbox[3]}] Z[{keep_bbox[4]},{keep_bbox[5]}]")
    print(f"Octree MaxDepth: {max_depth}, world size: {1 << max_depth}, origin offset: {origin}")
    print(f"Original terrain.dat: {len(terrain_data)} bytes")
    print()

    # Rewrite octree
    rewriter = TerrainRewriter(terrain_data, keep_bbox, origin)
    new_octree = rewriter.rewrite()
    octree_end = rewriter.octree_end_pos

    print("Octree rewrite stats:")
    for k, v in rewriter.stats.items():
        print(f"  {k}: {v}")
    print()

    # Rewrite vein data
    new_veins = rewrite_vein_data(terrain_data, octree_end, keep_bbox, origin)
    old_vein_count = struct.unpack_from("<i", terrain_data, octree_end)[0] if octree_end + 4 <= len(terrain_data) else 0
    new_vein_count = struct.unpack_from("<i", new_veins, 0)[0]
    print(f"Veins: {old_vein_count} original, {new_vein_count} kept")

    new_terrain = new_octree + new_veins
    print(f"New terrain.dat: {len(new_terrain)} bytes "
          f"({len(new_terrain) * 100 // len(terrain_data)}% of original)")
    print()

    # Zero checksums
    new_world_xml = zero_checksums(world_xml)

    if dry_run:
        print("Dry run complete. No files modified.")
        return

    # Backup and write
    backup = save.with_suffix(".save.bak")
    if not backup.exists():
        shutil.copy2(save, backup)
        print(f"Backup: {backup}")
    else:
        print(f"Backup already exists: {backup}")

    with zipfile.ZipFile(save, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("world.xml", new_world_xml.encode("utf-8"))
        zf.writestr("terrain.dat", new_terrain)
        for name, data in other_files.items():
            zf.writestr(name, data)

    print(f"Wrote: {save} ({save.stat().st_size} bytes)")
    print("Done.")


def main():
    parser = argparse.ArgumentParser(
        description="Reset Stationeers terrain outside sealed rooms."
    )
    parser.add_argument("save", help="Path to .save file")
    parser.add_argument(
        "--margin",
        type=int,
        default=3,
        help="Voxels of margin around room cells for walls (default: 3)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Analyze and report without modifying files",
    )
    args = parser.parse_args()
    process_save(args.save, margin=args.margin, dry_run=args.dry_run)


if __name__ == "__main__":
    main()
