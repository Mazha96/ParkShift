"""
Validate the FIXED two-block station: RVs park at block CENTRE (not the shared boundary),
and an unloaded RV WAITS at the Unload centre until the Load block frees, so two RVs never
intertwine. Mirrors the revised TrainSim (split path) + AttractionSim operator.

Position proxy along the loop = block + progress (mod B). Two RVs "overlap" if their circular
distance in this proxy is below MIN_SEP (~one third of a block).
"""
B = 5                  # Unload, Load, Launch, Course, Brake
LOAD_BLOCK = 1
PARK = 0.5             # station RVs park at block centre
SEATS = 8
BASE = 6.0
DT = 0.1
MIN_SEP = 0.30

LOAD, READY, RUN, HOLD, UNLOAD = "LOAD", "READY", "RUN", "HOLD", "UNLOAD"

class Blocks:
    def __init__(s, n): s.occ = [-1]*n; s.n = n
    def nxt(s, b): return (b+1) % s.n
    def clear(s, b): return s.occ[b] == -1
    def enter(s, b, t):
        if s.occ[b] != -1: return False
        s.occ[b] = t; return True
    def leave(s, b, t):
        if s.occ[b] == t: s.occ[b] = -1
    def place(s, b, t): s.occ[b] = t

class Train:
    def __init__(s, tid, blk, start, state, prog):
        s.id=tid; s.blk=blk; s.block=start; s.prog=prog; s.state=state; s.riders=0
        blk.place(start, tid)
    def may(s): return s.blk.clear(s.blk.nxt(s.block))
    def pos(s): return (s.block + s.prog) % B
    def tick(s, dt):
        # Split-station: an unloaded RV waits at the Unload centre until the Load block clears.
        if s.state == UNLOAD:
            if s.may(): s.state = RUN          # Load clear -> crawl from centre toward Load
            else: return False                  # park at Unload centre
        if s.state in (LOAD, READY): return False
        if s.state == HOLD and s.may(): s.state = RUN
        if s.state == RUN: s.prog += dt / BASE
        if s.prog >= 1.0:
            nb = s.blk.nxt(s.block)
            if s.blk.enter(nb, s.id):
                s.blk.leave(s.block, s.id); s.block = nb
                if s.block == 0:
                    s.prog = PARK; s.state = UNLOAD; return True   # arrived Unload, park centre
                if s.block == LOAD_BLOCK:
                    s.prog = PARK; s.state = LOAD; return False     # park at Load centre
                s.prog = 0.0; s.state = RUN
            else:
                s.prog = 1.0; s.state = HOLD
        return False

def circ(a, b):
    d = abs(a - b) % B
    return min(d, B - d)

def run(seconds=400):
    blk = Blocks(B)
    t0 = Train(0, blk, LOAD_BLOCK, LOAD, PARK)   # platform RV parked at Load centre
    t1 = Train(1, blk, B-1, HOLD, 1.0)           # staged on the brake run
    trains = [t0, t1]
    both_in_station = False
    unload_drop_ok = True
    min_sep = 99.0
    waited_at_unload_centre = False
    violation = False
    dispatches = 0

    for _ in range(int(seconds/DT)):
        load_rv = next((t for t in trains if t.block == LOAD_BLOCK and t.state in (LOAD, READY)), None)
        if load_rv:
            if load_rv.state == LOAD:
                load_rv.riders = SEATS
                load_rv.state = READY
            if load_rv.state == READY and blk.clear(blk.nxt(LOAD_BLOCK)):
                load_rv.state = RUN; dispatches += 1   # keep prog (rolls out from centre)

        for t in trains:
            if t.tick(DT):
                if t.block != 0: unload_drop_ok = False
                t.riders = 0          # dropped at Unload

        # an unloaded RV sitting at Unload centre while the Load block is occupied
        for t in trains:
            if t.state == UNLOAD and abs(t.prog - PARK) < 1e-6 and not blk.clear(LOAD_BLOCK):
                waited_at_unload_centre = True

        occ = [t.block for t in trains]
        if len(set(occ)) != len(occ): violation = True
        if any(t.block == 0 for t in trains) and any(t.block == LOAD_BLOCK for t in trains):
            both_in_station = True
        sep = circ(trains[0].pos(), trains[1].pos())
        if sep < min_sep: min_sep = sep

    return dict(violation=violation, both_in_station=both_in_station, unload_drop_ok=unload_drop_ok,
                min_sep=round(min_sep, 3), waited=waited_at_unload_centre, dispatches=dispatches)

if __name__ == "__main__":
    r = run()
    print("[1] no shared block: " + str(not r['violation']) + " -> " + ("PASS" if not r['violation'] else "FAIL"))
    print("[2] two RVs in station (Unload+Load): " + str(r['both_in_station']) + " -> " + ("PASS" if r['both_in_station'] else "FAIL"))
    print("[3] riders dropped at Unload(0): " + str(r['unload_drop_ok']) + " -> " + ("PASS" if r['unload_drop_ok'] else "FAIL"))
    print("[4] RVs never intertwine (min loop separation " + str(r['min_sep']) + " >= " + str(MIN_SEP) + "): -> " + ("PASS" if r['min_sep'] >= MIN_SEP else "FAIL"))
    print("[5] unloaded RV waits at Unload CENTRE until Load frees: " + str(r['waited']) + " -> " + ("PASS" if r['waited'] else "FAIL"))
    print("[6] dispatches happen: " + str(r['dispatches']) + " -> " + ("PASS" if r['dispatches'] > 0 else "FAIL"))
    ok = (not r['violation']) and r['both_in_station'] and r['unload_drop_ok'] and r['min_sep'] >= MIN_SEP and r['waited'] and r['dispatches'] > 0
    print("ALL PASS" if ok else "SOME FAILED")
