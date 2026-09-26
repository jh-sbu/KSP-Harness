"""Interplanetary transfer planning: Kepler propagation, Lambert solver, porkchop search, ejection geometry.

All vectors are in KSP's orbit frame (the frame of Orbit.getRelativePositionAtUT: z = celestial pole),
relative to the parent body of the orbit in question. Orbital elements are read from the game through
``eval`` and propagated here, so a porkchop search of thousands of Lambert solutions costs no game calls.
The game's patched-conic solver remains the final judge: plans are refined against ``node_approach``.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from .client import KSP


# ------------------------------------------------------------------ Kepler


@dataclass
class Elements:
    mu: float           # parent's gravitational parameter
    a: float
    e: float
    inc: float          # radians
    lan: float
    argp: float
    m0: float           # mean anomaly at epoch (radians)
    epoch: float
    parent: str = ""

    @property
    def period(self) -> float:
        return 2 * math.pi * math.sqrt(self.a ** 3 / self.mu)

    @classmethod
    def from_state(cls, r: np.ndarray, v: np.ndarray, ut: float, mu: float, parent: str = "") -> "Elements":
        """Elements (elliptical) from a state vector at UT, in whatever frame r and v are given."""
        R = np.linalg.norm(r)
        h = np.cross(r, v)
        H = np.linalg.norm(h)
        ev = np.cross(v, h) / mu - r / R
        e = float(np.linalg.norm(ev))
        a = 1 / (2 / R - float(np.dot(v, v)) / mu)
        inc = math.acos(np.clip(h[2] / H, -1, 1))
        nvec = np.array([-h[1], h[0], 0.0])
        N = np.linalg.norm(nvec)
        if N < 1e-9 * H:  # equatorial: node line along x
            nvec, N, lan = np.array([1.0, 0.0, 0.0]), 1.0, 0.0
        else:
            lan = math.atan2(nvec[1], nvec[0])
        if e < 1e-9:  # circular: periapsis at the node
            argp = 0.0
            eh = nvec / N
        else:
            eh = ev / e
            argp = math.atan2(float(np.dot(np.cross(nvec / N, eh), h / H)), float(np.dot(nvec / N, eh)))
        nu = math.atan2(float(np.dot(np.cross(eh, r / R), h / H)), float(np.dot(eh, r / R)))
        E = 2 * math.atan2(math.sqrt(1 - e) * math.sin(nu / 2), math.sqrt(1 + e) * math.cos(nu / 2))
        M = E - e * math.sin(E)
        return cls(mu=mu, a=a, e=e, inc=inc, lan=lan, argp=argp, m0=M, epoch=ut, parent=parent)

    def _frame(self) -> np.ndarray:
        cO, sO = math.cos(self.lan), math.sin(self.lan)
        ci, si = math.cos(self.inc), math.sin(self.inc)
        cw, sw = math.cos(self.argp), math.sin(self.argp)
        # columns: perifocal P, Q in the reference frame
        P = np.array([cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, sw * si])
        Q = np.array([-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, cw * si])
        return np.stack([P, Q])

    def state(self, ut: float) -> tuple[np.ndarray, np.ndarray]:
        """Position and velocity at UT (elliptical orbits only)."""
        n = math.sqrt(self.mu / self.a ** 3)
        M = self.m0 + n * (ut - self.epoch)
        M = math.fmod(M, 2 * math.pi)
        E = M if self.e < 0.8 else math.pi
        for _ in range(50):
            dE = (E - self.e * math.sin(E) - M) / (1 - self.e * math.cos(E))
            E -= dE
            if abs(dE) < 1e-13:
                break
        cE, sE = math.cos(E), math.sin(E)
        b = self.a * math.sqrt(1 - self.e ** 2)
        x, y = self.a * (cE - self.e), b * sE
        rdot = math.sqrt(self.mu * self.a) / (self.a * (1 - self.e * cE))
        vx, vy = -rdot * sE, rdot * math.sqrt(1 - self.e ** 2) * cE
        P, Q = self._frame()
        return x * P + y * Q, vx * P + vy * Q


def get_elements(k: KSP, expr: str) -> Elements:
    """Elements of an Orbit object reachable by an eval expression, e.g. 'FlightGlobals.Bodies[5].orbit'."""
    o = k.eval(expr=expr, members=True)
    parent_mu = k.eval(expr=expr + ".referenceBody.gravParameter")
    return Elements(mu=float(parent_mu), a=float(o["semiMajorAxis"]), e=float(o["eccentricity"]),
                    inc=math.radians(float(o["inclination"])), lan=math.radians(float(o["LAN"])),
                    argp=math.radians(float(o["argumentOfPeriapsis"])), m0=float(o["meanAnomalyAtEpoch"]),
                    epoch=float(o["epoch"]), parent=str(o["referenceBody"]).split(" (")[0])


def body_index(k: KSP, name: str) -> int:
    names = [s.split(" (")[0] for s in k.eval(expr="FlightGlobals.Bodies")]
    return names.index(name)


def body_elements(k: KSP, name: str, ut: float | None = None) -> Elements:
    """Elements of a body's orbit in the game's current orbit frame (built from its state vector, because
    the frame can be rotated relative to the raw LAN / mean anomaly values)."""
    expr = f"FlightGlobals.Bodies[{body_index(k, name)}].orbit"
    ut = k.ut() if ut is None else ut
    r, v = game_state(k, expr, ut)
    mu = float(k.eval(expr=expr + ".referenceBody.gravParameter"))
    return Elements.from_state(r, v, ut, mu, parent=str(k.eval(expr=expr + ".referenceBody.bodyName")))


def vessel_elements(k: KSP, ut: float | None = None) -> Elements:
    expr = "FlightGlobals.ActiveVessel.orbit"
    ut = k.ut() if ut is None else ut
    r, v = game_state(k, expr, ut)
    mu = float(k.eval(expr=expr + ".referenceBody.gravParameter"))
    return Elements.from_state(r, v, ut, mu, parent=str(k.eval(expr=expr + ".referenceBody.bodyName")))


def game_state(k: KSP, orbit_expr: str, ut: float) -> tuple[np.ndarray, np.ndarray]:
    r = k.eval(expr=f"{orbit_expr}.getRelativePositionAtUT({ut!r})")
    v = k.eval(expr=f"{orbit_expr}.getOrbitalVelocityAtUT({ut!r})")
    return _vec(r), _vec(v)


def _vec(x) -> np.ndarray:
    if isinstance(x, str):
        x = x.strip("[]() ").split(",")
    if isinstance(x, dict):
        x = [x["x"], x["y"], x["z"]]
    return np.array([float(c) for c in x])


# ------------------------------------------------------------------ Lambert (universal variables)


def _stumpff(z: float) -> tuple[float, float]:
    if z > 1e-6:
        s = math.sqrt(z)
        return (1 - math.cos(s)) / z, (s - math.sin(s)) / s ** 3
    if z < -1e-6:
        s = math.sqrt(-z)
        return (math.cosh(s) - 1) / (-z), (math.sinh(s) - s) / s ** 3
    return 0.5 - z / 24, 1 / 6 - z / 120


def lambert(r1: np.ndarray, r2: np.ndarray, tof: float, mu: float, prograde: bool = True):
    """Zero-revolution Lambert problem. Returns (v1, v2) or None if no solution was found."""
    R1, R2 = np.linalg.norm(r1), np.linalg.norm(r2)
    cross = np.cross(r1, r2)
    cos_dth = np.clip(np.dot(r1, r2) / (R1 * R2), -1, 1)
    dth = math.acos(cos_dth)
    if (cross[2] < 0) == prograde:
        dth = 2 * math.pi - dth
    A = math.sin(dth) * math.sqrt(R1 * R2 / (1 - math.cos(dth)))
    if abs(A) < 1e-9:
        return None

    def y(z):
        C, S = _stumpff(z)
        return R1 + R2 + A * (z * S - 1) / math.sqrt(C)

    def F(z):
        C, S = _stumpff(z)
        yz = y(z)
        if yz < 0:
            return None
        return (yz / C) ** 1.5 * S + A * math.sqrt(yz) - math.sqrt(mu) * tof

    # bracket: lower bound where y(z) >= 0, upper bound below (2π)^2
    lo, hi = -4 * math.pi ** 2, 4 * math.pi ** 2 - 1e-6
    while y(lo) < 0:
        lo = (lo + hi) / 2 if lo < 0 else lo + 0.1
        if lo > hi:
            return None
    # F is monotone increasing in z on the valid range
    flo = F(lo)
    tries = 0
    while flo is not None and flo > 0 and tries < 60:
        lo -= 10 * (1 + tries)
        flo = F(lo)
        tries += 1
    fhi = F(hi)
    if flo is None or fhi is None or flo > 0 or fhi < 0:
        # shrink lower bound to the feasibility edge
        a, b = -4 * math.pi ** 2 * 50, hi
        for _ in range(200):
            m = (a + b) / 2
            if y(m) < 0:
                a = m
            else:
                b = m
        lo = b
        flo = F(lo)
        if flo is None or flo > 0 or fhi is None or fhi < 0:
            return None
    for _ in range(200):
        m = (lo + hi) / 2
        fm = F(m)
        if fm is None or fm < 0:
            lo = m
        else:
            hi = m
        if hi - lo < 1e-12:
            break
    z = (lo + hi) / 2
    yz = y(z)
    f = 1 - yz / R1
    g = A * math.sqrt(yz / mu)
    gdot = 1 - yz / R2
    v1 = (r2 - f * r1) / g
    v2 = (gdot * r2 - r1) / g
    return v1, v2


# ------------------------------------------------------------------ hyperbolic departure / arrival


def dv_from_circular(vinf: float, mu: float, r: float) -> float:
    """Burn from a circular orbit of radius r onto a hyperbola with excess speed vinf (Oberth at r)."""
    return math.sqrt(vinf ** 2 + 2 * mu / r) - math.sqrt(mu / r)


def dv_from_circular_plane(vinf_vec: np.ndarray, mu: float, r: float, normal: np.ndarray | None) -> float:
    """Ejection cost from a circular orbit of radius r whose plane has unit normal `normal`, onto a
    hyperbola with excess velocity vinf_vec. The asymptote's declination d from the orbit plane has to be
    bought with a plane change at the burn: dv = |v_p - v_c| with angle d between them (best case)."""
    vinf = float(np.linalg.norm(vinf_vec))
    if normal is None:
        return dv_from_circular(vinf, mu, r)
    vp = math.sqrt(vinf ** 2 + 2 * mu / r)
    vc = math.sqrt(mu / r)
    sin_d = abs(float(np.dot(vinf_vec, normal))) / max(vinf, 1e-9)
    cos_d = math.sqrt(max(0.0, 1 - sin_d ** 2))
    return math.sqrt(max(0.0, vc * vc + vp * vp - 2 * vc * vp * cos_d))


def asymptote_out(r: np.ndarray, v: np.ndarray, mu: float) -> np.ndarray | None:
    """Unit vector of the outgoing asymptote of the (hyperbolic) orbit with state (r, v)."""
    R = np.linalg.norm(r)
    V2 = float(np.dot(v, v))
    if V2 - 2 * mu / R <= 0:
        return None
    ev = ((V2 - mu / R) * r - np.dot(r, v) * v) / mu
    e = np.linalg.norm(ev)
    h = np.cross(r, v)
    hn = h / np.linalg.norm(h)
    eh = ev / e
    nu = math.acos(-1 / e)
    return math.cos(nu) * eh + math.sin(nu) * np.cross(hn, eh)


def departure_velocities(r: np.ndarray, vinf_vec: np.ndarray, mu: float) -> list[np.ndarray]:
    """Velocities at position r that leave on a hyperbola whose outgoing asymptote is vinf_vec.
    The hyperbola lies in the plane of r and vinf; the asymptote angle is not monotonic in the flight
    path angle, so sample it and bisect every true zero crossing (there are usually two solutions)."""
    R = np.linalg.norm(r)
    vinf = np.linalg.norm(vinf_vec)
    u = vinf_vec / vinf
    rh = r / R
    n = np.cross(r, vinf_vec)
    if np.linalg.norm(n) < 1e-9:
        return []
    n /= np.linalg.norm(n)
    th = np.cross(n, rh)          # in-plane horizontal, in the direction of motion toward u
    V = math.sqrt(vinf ** 2 + 2 * mu / R)

    def vel(gamma: float) -> np.ndarray:
        return V * (math.sin(gamma) * rh + math.cos(gamma) * th)

    def angle_err(gamma: float) -> float:
        a = asymptote_out(r, vel(gamma), mu)
        return math.atan2(float(np.dot(np.cross(u, a), n)), float(np.dot(u, a)))

    N = 180
    gs = [-math.pi / 2 + 1e-3 + (math.pi - 2e-3) * i / N for i in range(N + 1)]
    es = [angle_err(g) for g in gs]
    out = []
    for i in range(N):
        e1, e2 = es[i], es[i + 1]
        if e1 == 0:
            out.append(vel(gs[i]))
            continue
        if e1 * e2 >= 0 or abs(e1 - e2) > math.pi:  # no crossing, or a wrap at +-pi
            continue
        lo, hi, flo = gs[i], gs[i + 1], e1
        for _ in range(60):
            m = (lo + hi) / 2
            fm = angle_err(m)
            if (fm > 0) == (flo > 0):
                lo, flo = m, fm
            else:
                hi = m
        out.append(vel((lo + hi) / 2))
    return out


def departure_velocity(r: np.ndarray, vinf_vec: np.ndarray, mu: float, v_now: np.ndarray | None = None):
    """The departure velocity closest to v_now (or the first found), or None."""
    sols = departure_velocities(r, vinf_vec, mu)
    if not sols:
        return None
    if v_now is None:
        return sols[0]
    return min(sols, key=lambda x: float(np.linalg.norm(x - v_now)))


@dataclass
class Ejection:
    ut: float
    dv_vec: np.ndarray       # in the parking orbit's frame
    dv: float
    prograde: float
    normal: float
    radial: float


def node_coords(r: np.ndarray, v: np.ndarray, dv: np.ndarray) -> tuple[float, float, float]:
    """(radial, normal, prograde) components as KSP maneuver nodes define them (see CmdOrbit.ToNodeCoords)."""
    pro = v / np.linalg.norm(v)
    # KSP's Orbit.GetOrbitNormal() is r x v in the orbit frame... node "normal" is along -h in zup? verified in-game.
    h = np.cross(r, v)
    nrm = h / np.linalg.norm(h)
    rad = r - pro * np.dot(r, pro)
    rad /= np.linalg.norm(rad)
    return float(np.dot(dv, rad)), float(np.dot(dv, nrm)), float(np.dot(dv, pro))


def plan_ejection(park: Elements, vinf_vec: np.ndarray, t0: float, span: float | None = None, samples: int = 720) -> Ejection:
    """Cheapest impulsive burn on the parking orbit (within [t0, t0+span]) that departs with the given
    hyperbolic excess velocity vector."""
    span = span or park.period
    best = None
    for i in range(samples):
        t = t0 + span * i / samples
        r, v = park.state(t)
        vd = departure_velocity(r, vinf_vec, park.mu, v)
        if vd is None:
            continue
        dv = vd - v
        m = float(np.linalg.norm(dv))
        if best is None or m < best[1]:
            best = (t, m, dv)
    if best is None:
        raise RuntimeError("no ejection solution")
    # refine by golden section around the best sample
    step = span / samples

    def cost(t):
        r, v = park.state(t)
        vd = departure_velocity(r, vinf_vec, park.mu, v)
        return (float(np.linalg.norm(vd - v)) if vd is not None else 1e9), vd, r, v

    lo, hi = best[0] - step, best[0] + step
    for _ in range(60):
        m1, m2 = lo + (hi - lo) * 0.382, lo + (hi - lo) * 0.618
        if cost(m1)[0] < cost(m2)[0]:
            hi = m2
        else:
            lo = m1
    t = (lo + hi) / 2
    c, vd, r, v = cost(t)
    dv = vd - v
    rad, nrm, pro = node_coords(r, v, dv)
    return Ejection(ut=t, dv_vec=dv, dv=c, prograde=pro, normal=nrm, radial=rad)


# ------------------------------------------------------------------ porkchop


@dataclass
class Transfer:
    depart_ut: float
    arrive_ut: float
    vinf_dep: np.ndarray
    vinf_arr: np.ndarray
    dv_depart: float
    dv_arrive: float

    @property
    def total(self) -> float:
        return self.dv_depart + self.dv_arrive


def porkchop(origin: Elements, target: Elements, t_from: float, t_to: float, r_park: float, mu_origin: float,
             r_capture: float, mu_target: float, capture_weight: float = 1.0,
             n_dep: int = 120, n_tof: int = 60, tof_range: tuple[float, float] | None = None,
             early_tolerance: float = 25.0, early_fraction: float = 0.10,
             park_normal: np.ndarray | None = None) -> Transfer:
    """Grid search over departure time and time of flight, then local refinement.
    Cost = ejection dv from a circular parking orbit (including the plane change needed when the
    departure asymptote is out of the parking plane, given `park_normal`) + capture dv into a circular
    orbit (weighted)."""
    mu = origin.mu
    a_h = (origin.a + target.a) / 2
    hohmann = math.pi * math.sqrt(a_h ** 3 / mu)
    lo_tof, hi_tof = tof_range or (0.5 * hohmann, 1.5 * hohmann)

    def evaluate(td: float, tof: float) -> Transfer | None:
        r1, v1p = origin.state(td)
        r2, v2p = target.state(td + tof)
        sol = lambert(r1, r2, tof, mu)
        if sol is None:
            return None
        v1, v2 = sol
        vinf_d, vinf_a = v1 - v1p, v2 - v2p
        dd = dv_from_circular_plane(vinf_d, mu_origin, r_park, park_normal)
        da = dv_from_circular(float(np.linalg.norm(vinf_a)), mu_target, r_capture)
        return Transfer(td, td + tof, vinf_d, vinf_a, dd, da)

    def cost(tr):
        return tr.dv_depart + capture_weight * tr.dv_arrive if tr else float("inf")

    rows = []  # best transfer per departure date
    for i in range(n_dep):
        td = t_from + (t_to - t_from) * i / max(1, n_dep - 1)
        row = None
        for j in range(n_tof):
            tof = lo_tof + (hi_tof - lo_tof) * j / max(1, n_tof - 1)
            tr = evaluate(td, tof)
            if tr and (row is None or cost(tr) < cost(row)):
                row = tr
        if row:
            rows.append(row)
    if not rows:
        raise RuntimeError("porkchop found no solution")
    # The search spans a whole synodic period, so it can contain two windows of similar cost (they differ by
    # ~10% with orbital eccentricity and inclination). Take the earliest departure within max(early_tolerance,
    # early_fraction * best) of the global best, then refine locally.
    c_min = min(cost(r) for r in rows)
    best = next(r for r in rows if cost(r) <= c_min + max(early_tolerance, early_fraction * c_min))
    td, tof = best.depart_ut, best.arrive_ut - best.depart_ut
    s_td, s_tof = (t_to - t_from) / n_dep, (hi_tof - lo_tof) / n_tof
    for _ in range(200):
        improved = False
        for ctd, ctof in ((td + s_td, tof), (td - s_td, tof), (td, tof + s_tof), (td, tof - s_tof)):
            if ctd < t_from:
                continue
            tr = evaluate(ctd, ctof)
            if tr and cost(tr) < cost(best):
                best, td, tof, improved = tr, ctd, ctof, True
        if not improved:
            s_td /= 2
            s_tof /= 2
            if s_td < 1 and s_tof < 1:
                break
    return best


# ------------------------------------------------------------------ in-game refinement


def nelder_mead(f, x0, step, iters=200, tol=1e-6):
    """Minimal Nelder-Mead minimizer (no scipy dependency)."""
    n = len(x0)
    pts = [np.array(x0, float)]
    for i in range(n):
        p = np.array(x0, float)
        p[i] += step[i]
        pts.append(p)
    vals = [f(p) for p in pts]
    for _ in range(iters):
        order = np.argsort(vals)
        pts = [pts[i] for i in order]
        vals = [vals[i] for i in order]
        if abs(vals[-1] - vals[0]) < tol:
            break
        c = sum(pts[:-1]) / n
        xr = c + (c - pts[-1])
        fr = f(xr)
        if fr < vals[0]:
            xe = c + 2 * (c - pts[-1])
            fe = f(xe)
            pts[-1], vals[-1] = (xe, fe) if fe < fr else (xr, fr)
        elif fr < vals[-2]:
            pts[-1], vals[-1] = xr, fr
        else:
            xc = c + 0.5 * (pts[-1] - c)
            fc = f(xc)
            if fc < vals[-1]:
                pts[-1], vals[-1] = xc, fc
            else:
                for i in range(1, n + 1):
                    pts[i] = pts[0] + 0.5 * (pts[i] - pts[0])
                    vals[i] = f(pts[i])
    i = int(np.argmin(vals))
    return pts[i], vals[i]


# ------------------------------------------------------------------ B-plane targeting


def closest_approach(ev: Elements, et: Elements, t0: float, t1: float, n: int = 400):
    """Closest approach between two Kepler orbits around the same body in [t0, t1]:
    (UT, miss vector vessel - target, relative velocity)."""
    best_t, best_d = t0, float("inf")
    for i in range(n + 1):
        t = t0 + (t1 - t0) * i / n
        d = float(np.linalg.norm(ev.state(t)[0] - et.state(t)[0]))
        if d < best_d:
            best_t, best_d = t, d
    step = (t1 - t0) / n
    lo, hi = max(t0, best_t - step), min(t1, best_t + step)
    dist = lambda t: float(np.linalg.norm(ev.state(t)[0] - et.state(t)[0]))
    for _ in range(80):
        m1, m2 = lo + (hi - lo) * 0.382, lo + (hi - lo) * 0.618
        if dist(m1) < dist(m2):
            hi = m2
        else:
            lo = m1
    t = (lo + hi) / 2
    rv, vv = ev.state(t)
    rt, vt = et.state(t)
    return t, rv - rt, vv - vt


def bplane_aim(vrel: np.ndarray, r_pe: float, mu_target: float, prograde: bool = True) -> np.ndarray:
    """Aim point in the B-plane (miss vector perpendicular to the arrival velocity) that gives periapsis
    radius r_pe, oriented so the arrival orbit is prograde and as equatorial as possible (h along +z)."""
    vinf = float(np.linalg.norm(vrel))
    b = r_pe * math.sqrt(1 + 2 * mu_target / (r_pe * vinf ** 2))
    vh = vrel / vinf
    d = np.cross(vh, np.array([0.0, 0.0, 1.0]))
    if np.linalg.norm(d) < 1e-6:
        d = np.cross(vh, np.array([1.0, 0.0, 0.0]))
    d /= np.linalg.norm(d)
    # h = r x v ~ B x v_inf; B = d gives h ~ +z (prograde)
    return b * (d if prograde else -d)


def plan_bplane(r1: np.ndarray, v1: np.ndarray, ut: float, mu: float, target: Elements, t_window: tuple[float, float],
                r_pe: float, mu_target: float, prograde: bool = True, tol: float = 2000.0, iters: int = 30,
                max_step: float = 200.0, in_plane: bool = False) -> tuple[np.ndarray, dict]:
    """Minimum-norm velocity change at (r1, v1, ut) that makes the (target-gravity-free) trajectory pass the
    target with the B-plane miss vector for periapsis r_pe. Arrival time is free, so there is no 180-degree
    Lambert singularity. Returns (dv vector, info).

    in_plane=True restricts the velocity change to the current orbital plane and targets only the in-plane
    part of the miss (arrival timing and radius): the well-conditioned half of the problem, for departure
    burns whose out-of-plane error is cheaper to fix mid-course."""
    n_hat = np.cross(r1, v1)
    n_hat /= np.linalg.norm(n_hat)
    basis = [v1 / np.linalg.norm(v1), np.cross(n_hat, v1 / np.linalg.norm(v1))] if in_plane else list(np.eye(3))

    def miss(dv: np.ndarray):
        try:
            e = Elements.from_state(r1, v1 + dv, ut, mu)
            if not (0 <= e.e < 1):
                raise ValueError("not elliptical")
            t, d, vrel = closest_approach(e, target, *t_window)
        except (ValueError, ZeroDivisionError):
            return np.full(3, 1e12), np.array([1e4, 0, 0]), t_window[1]
        vh = vrel / np.linalg.norm(vrel)
        return d - vh * float(np.dot(d, vh)), vrel, t

    def residual(dv: np.ndarray):
        dperp, vrel, t = miss(dv)
        if in_plane:
            r = dperp - n_hat * float(np.dot(dperp, n_hat))  # in-plane miss; aim at the target itself
        else:
            r = dperp - bplane_aim(vrel, r_pe, mu_target, prograde)
        return r, vrel, t

    dv = np.zeros(3)
    info: dict = {}
    for it in range(iters):
        err, vrel, t = residual(dv)
        info = {"iterations": it, "miss_error_m": float(np.linalg.norm(err)), "arrival_ut": t,
                "vinf": float(np.linalg.norm(vrel)), "dv": float(np.linalg.norm(dv))}
        if np.linalg.norm(err) < tol:
            break
        J = np.zeros((3, len(basis)))
        h = 0.05
        for j, b in enumerate(basis):
            J[:, j] = (residual(dv + h * b)[0] - err) / h
        coef = -np.linalg.pinv(J, rcond=1e-6) @ err
        step = sum(c * b for c, b in zip(coef, basis))
        n = float(np.linalg.norm(step))
        if n > max_step:
            step *= max_step / n
        dv = dv + step
    return dv, info


def plan_inplane_intercept(r1: np.ndarray, v1: np.ndarray, ut: float, mu: float, target: Elements, t_arr: float,
                           iters: int = 30, tol: float = 50e3, max_step: float = 300.0) -> tuple[np.ndarray, dict]:
    """Velocity change within the current orbital plane that brings the in-plane projection of the position at
    t_arr onto the target's (a 2-D intercept: fixes arrival timing and radius, leaves the out-of-plane miss for a
    mid-course plane change). Well conditioned even for transfers near 180 degrees."""
    n_hat = np.cross(r1, v1)
    n_hat /= np.linalg.norm(n_hat)
    basis = [v1 / np.linalg.norm(v1), np.cross(n_hat, v1 / np.linalg.norm(v1))]
    rt, _ = target.state(t_arr)

    def residual(dv: np.ndarray) -> np.ndarray:
        try:
            e = Elements.from_state(r1, v1 + dv, ut, mu)
            if not (0 <= e.e < 1):
                raise ValueError
            d = e.state(t_arr)[0] - rt
        except (ValueError, ZeroDivisionError):
            return np.full(3, 1e13)
        return d - n_hat * float(np.dot(d, n_hat))

    dv = np.zeros(3)
    err = residual(dv)
    info = {"miss_m": float(np.linalg.norm(err)), "iterations": 0, "dv": 0.0}
    for it in range(iters):
        if np.linalg.norm(err) < tol:
            break
        J = np.zeros((3, 2))
        h = 0.05
        for j, b in enumerate(basis):
            J[:, j] = (residual(dv + h * b) - err) / h
        coef = -np.linalg.pinv(J, rcond=1e-8) @ err
        step = coef[0] * basis[0] + coef[1] * basis[1]
        n = float(np.linalg.norm(step))
        if n > max_step:
            step *= max_step / n
        dv = dv + step
        err = residual(dv)
        info = {"miss_m": float(np.linalg.norm(err)), "iterations": it + 1, "dv": float(np.linalg.norm(dv))}
    return dv, info
