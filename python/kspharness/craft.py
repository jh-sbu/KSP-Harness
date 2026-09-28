"""Generate KSP .craft files from a small Python description.

Geometry (attach-node positions, surface-attach directions) comes from the running game via
``part_info``, so parts line up exactly as the editor would place them. Only stack attachment
along the vertical axis and radial (surface) attachment with N-way symmetry are supported,
which covers rockets.

    from kspharness.craft import Craft
    c = Craft("My Rocket", ksp)
    pod = c.root("mk1pod.v2")
    tank = pod.stack("MK1Fuselage")            # below pod (pod.bottom <- tank.top)
    eng = tank.stack("nuclearEngine", stage=2)
    tank.radial("solarPanels5", count=2, y=0.3)
    c.save(path)

Staging: pass ``stage=N`` to parts that activate (engines, decouplers, chutes). N is the
KSP inverse stage number (the highest fires first). Other parts inherit the stage of the
decoupler that drops them, as the editor does.
"""

from __future__ import annotations

import math
import random
from pathlib import Path

Vec = tuple[float, float, float]
Quat = tuple[float, float, float, float]


def _add(a: Vec, b: Vec) -> Vec:
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def _sub(a: Vec, b: Vec) -> Vec:
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _yaw_quat(deg: float) -> Quat:
    """Rotation about +Y (Unity's up) by `deg` degrees, as (x, y, z, w)."""
    h = math.radians(deg) / 2
    return (0.0, math.sin(h), 0.0, math.cos(h))


def _rot_y(v: Vec, deg: float) -> Vec:
    """Rotate v about +Y the way Unity's Quaternion.AngleAxis(deg, up) does (left-handed)."""
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return (v[0] * c + v[2] * s, v[1], -v[0] * s + v[2] * c)


def _fmt(x: float) -> str:
    if abs(x) < 1e-9:
        return "0"
    return f"{x:.6f}".rstrip("0").rstrip(".")


def _v(v) -> str:
    return ",".join(_fmt(x) for x in v)


class Part:
    def __init__(self, craft: "Craft", name: str, stage: int | None = None):
        self.craft = craft
        self.name = name
        self.info = craft.info(name)
        self.uid = f"{name}_{craft.next_id()}"
        self.pos: Vec = (0.0, 0.0, 0.0)
        self.rot: Quat = (0.0, 0.0, 0.0, 1.0)
        self.parent: Part | None = None
        self.children: list[Part] = []
        self.att_nodes: list[tuple[str, "Part"]] = []  # (my node id, other part)
        self.srf_parent: Part | None = None
        self.sym: list[Part] = []
        self.stage = stage          # activation stage (engines, decouplers, chutes)
        self.decouple_stage = -1    # computed: stage at which this part leaves the vessel
        self.persistent_id = random.randint(10**8, 4 * 10**9)
        self.extra: list[str] = []  # raw extra lines (e.g. MODULE blocks)
        self.yaw = 0.0
        self.outward: Vec | None = None  # outward normal of a radially attached part
        self.autostrut = "Off"  # Off | Root | Heaviest | Grandparent
        self.flip = False
        self.rigid = False

    # -------------------------------------------------------------- geometry helpers
    def node(self, nid: str) -> dict:
        for n in self.info["nodes"]:
            if n["id"] == nid:
                return n
        raise KeyError(f"{self.name} has no node '{nid}' (has {[n['id'] for n in self.info['nodes']]})")

    def has_node(self, nid: str) -> bool:
        return any(n["id"] == nid for n in self.info["nodes"])

    @property
    def radius(self) -> float:
        """Approximate radius from node size (0=0.3125, 1=0.625, 2=1.25, 3=1.875, 4=2.5)."""
        sizes = [n["size"] for n in self.info["nodes"]] or [1]
        return {0: 0.3125, 1: 0.625, 2: 1.25, 3: 1.875, 4: 2.5}.get(max(sizes), 0.625)

    # -------------------------------------------------------------- building
    def stack(self, name: str, my_node: str = "bottom", its_node: str = "top", stage: int | None = None,
              my_node_y: float | None = None, flip: bool = False) -> "Part":
        """Attach a part to one of this part's stack nodes (default: below this part). `my_node_y` overrides the
        node height when a part variant moves it (part_info reports the prefab's default nodes). `flip` mounts the
        child upside down (180 degrees about X; only for parts on an unrotated parent)."""
        child = Part(self.craft, name, stage)
        mn, cn = self.node(my_node), child.node(its_node)
        mpos = tuple(mn["pos"]) if my_node_y is None else (mn["pos"][0], my_node_y, mn["pos"][2])
        cpos = tuple(cn["pos"])
        if flip:
            child.flip = True
            child.rot = (1.0, 0.0, 0.0, 0.0)
            cpos = (cpos[0], -cpos[1], -cpos[2])
        child.pos = _sub(_add(self.pos, mpos), cpos)
        child.parent = self
        self.children.append(child)
        self.att_nodes.append((my_node, child))
        child.att_nodes.append((its_node, self))
        self.craft.parts.append(child)
        return child

    def radial(self, name: str, count: int = 1, y: float = 0.0, radius: float | None = None, angle0: float = 0.0,
               stage: int | None = None, arc: float = 360.0) -> list["Part"]:
        """Surface-attach `count` copies around this part at height `y` (part-local), spread evenly over `arc`
        degrees starting at `angle0` (full circle: radial symmetry)."""
        r = self.radius if radius is None else radius
        out = []
        for i in range(count):
            ang = angle0 + (360.0 * i / count if arc >= 360.0 else (arc * i / (count - 1) if count > 1 else 0.0))
            normal = _rot_y((0.0, 0.0, 1.0), ang)
            surface = _add(self.pos, (normal[0] * r, y, normal[2] * r))
            out.append(self._srf_attach(name, surface, normal, stage))
        for p in out:
            p.sym = [q for q in out if q is not p]
        return out

    def _srf_attach(self, name: str, surface: Vec, normal: Vec, stage: int | None) -> "Part":
        """Surface-attach a part at world point `surface`, where this part's outward normal is `normal`.
        As in the editor (see the stock Kerbal X), the child's srfAttachNode direction points back at the
        parent, so the child is yawed to map that direction onto -normal."""
        child = Part(self.craft, name, stage)
        srf = child.info.get("srf_node") or {"pos": [0, 0, 0], "dir": [0, 0, -1]}
        d = tuple(srf["dir"])
        ang = math.degrees(math.atan2(normal[0], normal[2]))
        d_ang = math.degrees(math.atan2(d[0], d[2]))  # heading of the srf dir in the XZ plane (Unity yaw)
        yaw = ang + 180.0 - d_ang
        child.rot = _yaw_quat(yaw)
        child.yaw = yaw
        child.outward = normal
        child.pos = _sub(surface, _rot_y(tuple(srf["pos"]), yaw))
        child.parent = self
        child.srf_parent = self
        self.children.append(child)
        self.craft.parts.append(child)
        return child

    def side(self, name: str, depth: float, stage: int | None = None) -> "Part":
        """Surface-attach a part to the outer face of this radially attached part (e.g. a booster tank on a
        radial decoupler). `depth` is the distance from this part's origin to its outer face."""
        if self.outward is None:
            raise ValueError(f"{self.name} was not radially attached")
        n = self.outward
        return self._srf_attach(name, _add(self.pos, (n[0] * depth, 0.0, n[2] * depth)), n, stage)

    def sym_side(parts: list["Part"], name: str, depth: float, stage: int | None = None) -> list["Part"]:
        """side() on every part of a symmetry group; the new parts form their own symmetry group."""
        out = [p.side(name, depth, stage) for p in parts]
        for p in out:
            p.sym = [q for q in out if q is not p]
        return out

    def sym_stack(parts: list["Part"], name: str, my_node: str = "bottom", its_node: str = "top",
                  stage: int | None = None) -> list["Part"]:
        """stack() on every part of a symmetry group, keeping the group's yaw."""
        out = []
        for p in parts:
            c = p.stack(name, my_node, its_node, stage)
            c.rot, c.yaw, c.outward = p.rot, p.yaw, p.outward
            # stack offsets are along +Y, which the yaw leaves unchanged
            out.append(c)
        for c in out:
            c.sym = [q for q in out if q is not c]
        return out


class Craft:
    def __init__(self, name: str, ksp=None, part_info: dict | None = None, description: str = ""):
        self.name = name
        self.description = description
        self.ksp = ksp
        self._info = dict(part_info or {})
        self.parts: list[Part] = []
        self._id = 4294000000 + random.randint(0, 100000)

    def next_id(self) -> int:
        self._id += 17
        return self._id

    def info(self, name: str) -> dict:
        if name not in self._info:
            if self.ksp is None:
                raise KeyError(f"no part info for {name} and no game connection")
            self._info.update(self.ksp.call("part_info", names=[name]))
        return self._info[name]

    def root(self, name: str, stage: int | None = None) -> Part:
        p = Part(self, name, stage)
        self.parts.insert(0, p)
        return p

    # -------------------------------------------------------------- staging
    def _compute_stages(self) -> None:
        """Each part's decouple stage = activation stage of the nearest decoupler between it and the root
        whose explosive node faces the root side."""
        root = self.parts[0]

        def walk(p: Part, dstage: int) -> None:
            p.decouple_stage = dstage
            for c in p.children:
                d = dstage
                if "decoupler" in c.info and c.stage is not None:
                    # a decoupler attached to its parent by its explosive node leaves with the child side
                    my = next((n for n, o in c.att_nodes if o is p), None)
                    if my == c.info["decoupler"]["node"] or c.srf_parent is p:
                        d = c.stage
                walk(c, d)
        walk(root, -1)

    # -------------------------------------------------------------- output
    def text(self) -> str:
        self._compute_stages()
        lo = min(p.pos[1] for p in self.parts)
        hi = max(p.pos[1] for p in self.parts)
        out = [
            f"ship = {self.name}",
            "version = 1.12.5",
            f"description = {self.description}",
            "type = VAB",
            f"size = 3,{_fmt(hi - lo + 2)},3",
            "steamPublishedFileId = 0",
            f"persistentId = {random.randint(10**8, 4 * 10**9)}",
            "rot = 0,0,0,1",
            "missionFlag = Squad/Flags/default",
            "vesselType = Ship",
        ]
        # sidx: order within a stage
        by_stage: dict[int, int] = {}
        for p in self.parts:
            parent = p.parent
            # attPos0/attRot0 are in the parent's frame (as the editor writes them)
            attpos0 = p.pos if parent is None else _rot_y(_sub(p.pos, parent.pos), -parent.yaw)
            attrot0 = p.rot if parent is None or p.flip else _yaw_quat(p.yaw - parent.yaw)
            staged = p.stage is not None
            istg = p.stage if staged else p.decouple_stage
            sidx = -1
            if staged:
                sidx = by_stage.get(p.stage, 0)
                by_stage[p.stage] = sidx + 1
            lines = [
                "PART",
                "{",
                f"\tpart = {p.uid}",
                "\tpartName = Part",
                f"\tpersistentId = {p.persistent_id}",
                f"\tpos = {_v(p.pos)}",
                "\tattPos = 0,0,0",
                f"\tattPos0 = {_v(attpos0)}",
                f"\trot = {_v(p.rot)}",
                "\tattRot = 0,0,0,1",
                f"\tattRot0 = {_v(attrot0)}",
                "\tmir = 1,1,1",
                "\tsymMethod = Radial",
                f"\tautostrutMode = {p.autostrut}",
                f"\trigidAttachment = {'True' if p.rigid else 'False'}",
                f"\tistg = {istg}",
                "\tresPri = 0",
                f"\tdstg = {max(p.decouple_stage, 0)}",
                f"\tsidx = {sidx}",
                f"\tsqor = {p.stage if staged else -1}",
                f"\tsepI = {p.decouple_stage}",
                f"\tattm = {1 if p.srf_parent is not None else 0}",
                "\tmodCost = 0",
                "\tmodMass = 0",
                "\tmodSize = 0,0,0",
            ]
            for c in p.children:
                lines.append(f"\tlink = {c.uid}")
            for nid, other in p.att_nodes:
                lines.append(f"\tattN = {nid},{other.uid}_0|{_fmt(p.node(nid)['pos'][1])}|0")
            for s in p.sym:
                lines.append(f"\tsym = {s.uid}")
            if p.srf_parent is not None:
                lines.append(f"\tsrfN = srfAttach,{p.srf_parent.uid}")
            lines += ["\tEVENTS", "\t{", "\t}", "\tACTIONS", "\t{", "\t}", "\tPARTDATA", "\t{", "\t}"]
            lines += p.extra
            lines.append("}")
            out += lines
        return "\n".join(out) + "\n"

    def save(self, path: str | Path) -> Path:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(self.text())
        return path

    # -------------------------------------------------------------- analysis
    def summary(self) -> dict:
        wet = sum(p.info["wet_mass"] for p in self.parts)
        dry = sum(p.info["dry_mass"] for p in self.parts)
        return {"parts": len(self.parts), "wet_mass": round(wet, 3), "dry_mass": round(dry, 3),
                "height": round(max(p.pos[1] for p in self.parts) - min(p.pos[1] for p in self.parts), 2)}
