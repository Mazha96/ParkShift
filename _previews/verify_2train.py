"""
Reproduce + verify the 2nd-train glitch fix. Split station: block0=Unload, block1=Load.
Train0 starts at Load (will be boarded); Train1 starts staged on the brake run EMPTY.
The bug: the empty staged RV reached Unload and waited forever for an unload it never needs,
so it never got to Load. Fix: empty RVs pass straight through Unload (UnloadComplete=true).
Assert BOTH trains reach Load and get boarded (the ride actually cycles).
"""
B = 5; UNLOAD = 0; LOAD = 1; PARK = 0.5
SEATS = 8; BASE = 6.0; DT = 0.1; UNLOAD_SECONDS = 1.0

LOAD_S, READY, RUN, HOLD, UNLOAD_S = "LOAD", "READY", "RUN", "HOLD", "UNLOAD"

class Blocks:
    def __init__(s, n): s.occ=[-1]*n; s.n=n
    def nxt(s,b): return (b+1)%s.n
    def clear(s,b): return s.occ[b]==-1
    def enter(s,b,t):
        if s.occ[b]!=-1: return False
        s.occ[b]=t; return True
    def leave(s,b,t):
        if s.occ[b]==t: s.occ[b]=-1
    def place(s,b,t): s.occ[b]=t

class Train:
    def __init__(s,tid,blk,start,state,prog):
        s.id=tid; s.blk=blk; s.block=start; s.state=state; s.prog=prog; s.riders=0
        s.unload_complete=True; blk.place(start,tid)
    def may(s): return s.blk.clear(s.blk.nxt(s.block))
    def tick(s,dt):
        if s.state==UNLOAD_S:
            if s.unload_complete and s.may(): s.state=RUN
            else: return False
        if s.state in (LOAD_S, READY): return False
        if s.state==HOLD and s.may(): s.state=RUN
        if s.state==RUN: s.prog += dt/BASE
        if s.prog>=1.0:
            nb=s.blk.nxt(s.block)
            if s.blk.enter(nb,s.id):
                s.blk.leave(s.block,s.id); s.block=nb
                if s.block==UNLOAD: s.prog=PARK; s.state=UNLOAD_S; return True
                if s.block==LOAD:   s.prog=PARK; s.state=LOAD_S; return False
                s.prog=0.0; s.state=RUN
            else:
                s.prog=1.0; s.state=HOLD
        return False

blk=Blocks(B)
t0=Train(0,blk,LOAD,LOAD_S,PARK)
t1=Train(1,blk,B-1,HOLD,1.0)
trains=[t0,t1]

# global unload workflow state
door=False; released=False; area_clear=True; guests_unloading=False; timer=0.0
boarded_at_load = {0:False, 1:False}
reached_load = {0:False, 1:False}

for step in range(6000):
    # operator at Load: board + dispatch
    lrv = next((t for t in trains if t.block==LOAD and t.state in (LOAD_S,READY)), None)
    if lrv:
        reached_load[lrv.id]=True
        if lrv.state==LOAD_S:
            lrv.riders=SEATS; boarded_at_load[lrv.id]=True; lrv.state=READY
        if lrv.state==READY and blk.clear(blk.nxt(LOAD)):
            lrv.state=RUN

    # operator at Unload: run the workflow only for an RV with riders
    urv = next((t for t in trains if t.block==UNLOAD and t.state==UNLOAD_S), None)
    if urv and urv.riders>0 and not released:
        door=True; released=True; guests_unloading=True; area_clear=False; timer=UNLOAD_SECONDS
        urv.riders=0
    # timer -> area clear -> RV may leave
    if guests_unloading:
        timer-=DT
        if timer<=0:
            guests_unloading=False; area_clear=True; released=False; door=False
            u=next((t for t in trains if t.block==UNLOAD and t.state==UNLOAD_S), None)
            if u: u.unload_complete=True

    # advance trains; arrival handling (empty pass-through fix)
    for t in trains:
        if t.tick(DT):
            if t.riders>0:
                t.unload_complete=False        # loaded: wait for workflow
            else:
                t.unload_complete=True          # empty: pass straight through  <-- the fix

print("[1] train0 reached Load: " + str(reached_load[0]) + " -> " + ("PASS" if reached_load[0] else "FAIL"))
print("[2] train1 reached Load (was the glitch): " + str(reached_load[1]) + " -> " + ("PASS" if reached_load[1] else "FAIL"))
print("[3] train1 got boarded at Load: " + str(boarded_at_load[1]) + " -> " + ("PASS" if boarded_at_load[1] else "FAIL"))
print("[4] both RVs cycle: " + str(boarded_at_load[0] and boarded_at_load[1]) + " -> " + ("PASS" if (boarded_at_load[0] and boarded_at_load[1]) else "FAIL"))
ok = reached_load[0] and reached_load[1] and boarded_at_load[0] and boarded_at_load[1]
print("ALL PASS" if ok else "SOME FAILED")
