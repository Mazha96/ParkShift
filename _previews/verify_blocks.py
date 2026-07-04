import math
B=6; BASE=6.0; MULT=[1.0,1.6,1.15,1.15,1.15,0.55]
BP=0.92; DECEL=0.6; ACCEL=0.9; DT=1.0/60.0
LOAD,READY,RUN,HOLD,UNLOAD="LOAD","READY","RUN","HOLD","UNLOAD"
def trav(b): return BASE/MULT[b]
class Blocks:
    def __init__(s,n): s.occ=[-1]*n; s.n=n
    def nxt(s,b): return (b+1)%s.n
    def clear(s,b): return s.occ[b]==-1
    def enter(s,b,t):
        if s.occ[b]!=-1: return False
        s.occ[b]=t; return True
    def leave(s,b,t):
        if s.occ[b]==t: s.occ[b]=-1
    def place(s,b,t): s.occ[b]=t
class Train:
    def __init__(s,tid,blk,start,prog,st):
        s.id=tid; s.blk=blk; s.block=start; s.prog=prog; s.spd=0.0; s.st=st; blk.place(start,tid)
    def may(s): return s.blk.clear(s.blk.nxt(s.block))
    def ease(s,dt):
        vfull=1.0/trav(s.block); bp=min(1.0,BP) if BP>0 else 1.0
        if s.may():
            vt=vfull
            if s.st==HOLD: s.st=RUN
        else:
            rem=max(0.0,bp-s.prog); vt=min(vfull, math.sqrt(2.0*DECEL*rem))
        s.spd=min(vt, s.spd+ACCEL*dt) if vt>s.spd else vt
        s.prog+=s.spd*dt
        if (not s.may()) and s.prog>=bp and bp<1.0:
            s.prog=bp; s.spd=0.0; s.st=HOLD
    def tick(s,dt):
        if s.st in (LOAD,READY,UNLOAD): return False
        s.ease(dt)
        if s.prog>=1.0:
            n=s.blk.nxt(s.block)
            if s.blk.enter(n,s.id):
                s.blk.leave(s.block,s.id); s.block=n; s.prog=0.0
                if s.st==HOLD: s.st=RUN
                if s.block==0: s.st=UNLOAD; s.spd=0.0; return True
            else:
                s.prog=1.0; s.spd=0.0; s.st=HOLD
        return False

def coast():
    blk=Blocks(B); blk.place(2,99); t=Train(0,blk,1,0.0,RUN)
    mx=0.0; prev=1e9; smooth=True; ramped=False
    for _ in range(20000):
        t.tick(DT); mx=max(mx,t.prog)
        if t.spd>0.05: ramped=True
        if ramped and t.st==RUN and t.spd>prev+1e-6 and t.prog>0.5: smooth=False
        prev=t.spd
        if t.st==HOLD: break
    ok=abs(t.prog-BP)<1e-3 and mx<=BP+1e-3 and t.spd<1e-6 and t.st==HOLD and smooth
    print("[1] coast stop_at="+str(round(t.prog,4))+" target="+str(BP)+" max="+str(round(mx,4))+" spd="+str(round(t.spd,6))+" -> "+("PASS" if ok else "FAIL"))
    return ok

def run(n,secs,load=2.0,warm=60.0):
    blk=Blocks(B); trains=[Train(0,blk,0,0.0,LOAD)]
    for i in range(1,n):
        st=B-i
        while st>0 and not blk.clear(st): st-=1
        trains.append(Train(i,blk,st,BP,HOLD))
    lt=0.0; done=0; held=0.0; perblk=[0.0]*B; bad=False
    steps=int(secs/DT); wsteps=int(warm/DT)
    for step in range(steps):
        meas=step>wsteps
        s0=next((t for t in trains if t.block==0 and t.st in (LOAD,READY,UNLOAD)),None)
        if s0:
            if s0.st==UNLOAD: s0.st=LOAD; lt=0.0
            elif s0.st==LOAD:
                lt+=DT
                if lt>=load: s0.st=READY
            elif s0.st==READY and blk.clear(blk.nxt(0)):
                s0.st=RUN; s0.prog=0.0; s0.spd=0.0
        for t in trains:
            if t.tick(DT) and meas: done+=1
        if meas:
            for t in trains:
                if t.st==HOLD: held+=DT; perblk[t.block]+=DT
        occ=[t.block for t in trains]
        if len(set(occ))!=len(occ): bad=True
    return dict(done=done,held=held,perblk=perblk,bad=bad)

def show(p):
    nm=["Station","Lift","CrsA","CrsB","CrsC","Brake"]
    return " ".join(nm[i]+":"+str(round(p[i]))+"s" for i in range(B))

def cycling():
    r3=run(3,600); r4=run(4,600)
    safe=(not r3['bad']) and (not r4['bad'])
    print("[2] no co-occupancy: 3RV="+str(not r3['bad'])+" 4RV="+str(not r4['bad'])+" -> "+("PASS" if safe else "FAIL"))
    print("[3] steady 540s (60s warmup skipped):")
    print("    3RV done="+str(r3['done'])+" held="+str(round(r3['held'],1))+"s | "+show(r3['perblk']))
    print("    4RV done="+str(r4['done'])+" held="+str(round(r4['held'],1))+"s | "+show(r4['perblk']))
    clean=r3['held']<15
    back=sum(r4['perblk'][3:])
    stack=r4['held']>5*max(1.0,r3['held']) and r4['held']>100 and back>0.5*r4['held']
    print("    3RV clean(held<15s)="+str(clean)+"  4RV stacking(>5x,back-half)="+str(stack)+" -> "+("PASS" if (clean and stack) else "FAIL"))
    return safe and clean and stack

a=coast(); b=cycling()
print("ALL PASS" if (a and b) else "SOME FAILED")
