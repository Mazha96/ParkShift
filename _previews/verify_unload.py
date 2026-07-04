"""
Validate the gated Unload workflow (mirrors AttractionSim verbs + TrainSim.UnloadComplete gate).
Guests stay aboard until the operator OPENS the door AND RELEASES restraints; the RV cannot leave
Unload until the area is clear; the door cannot close until the area is clear.
"""
UNLOAD_SECONDS = 4.0
DT = 0.1
SEATS = 8

class Sim:
    def __init__(s):
        # RV parked at Unload with riders aboard, door closed, restraints locked.
        s.rv_riders = SEATS
        s.rv_at_unload = True
        s.unload_complete = False        # TrainSim.UnloadComplete
        s.door_open = False
        s.restraints_released = False
        s.guests_unloading = False
        s.area_clear = False
        s.timer = 0.0
        s.served = 0
        s.left_unload = False

    # verbs (return True on success)
    def open_door(s):
        if not s.rv_at_unload or s.door_open: return False
        s.door_open = True; return True
    def close_door(s):
        if not s.door_open or not s.area_clear: return False
        s.door_open = False; s.restraints_released = False; return True
    def release_restraints(s):
        if not s.rv_at_unload or not s.door_open or s.restraints_released: return False
        s.restraints_released = True; s.guests_unloading = True; s.area_clear = False
        s.timer = UNLOAD_SECONDS
        s.served += s.rv_riders; s.rv_riders = 0
        return True

    def tick(s, dt):
        if s.guests_unloading:
            s.timer -= dt
            if s.timer <= 0:
                s.guests_unloading = False; s.area_clear = True; s.unload_complete = True
        # RV leaves Unload only when guests cleared (UnloadComplete) [Load assumed clear here]
        if s.rv_at_unload and s.unload_complete:
            s.rv_at_unload = False; s.left_unload = True

results = {}
s = Sim()

# [1] guests retained on arrival
results['retained_on_arrival'] = (s.rv_riders == SEATS and not s.unload_complete)

# [2] cannot release restraints before the door is open
results['release_blocked_door_closed'] = (s.release_restraints() == False)

# [2b] cannot close door before opening / no premature actions; open the door
results['open_ok'] = s.open_door()

# [3] restraints release now works, drops riders, starts the timer; RV still parked
ok_release = s.release_restraints()
results['release_after_open'] = ok_release and s.rv_riders == 0 and s.guests_unloading

# [4] close-door blocked while guests are still clearing
results['close_blocked_midunload'] = (s.close_door() == False)

# [5] RV must NOT leave Unload before the area is clear
s.tick(DT)  # 0.1s in
results['rv_waits_during_unload'] = (s.rv_at_unload == True and not s.area_clear)

# run out the unload timer
t = 0.0
while t < UNLOAD_SECONDS + 1.0 and not s.area_clear:
    s.tick(DT); t += DT

# [6] after the timer: area clear, RV freed, door can close
results['area_clear_after_timer'] = s.area_clear
results['rv_left_after_clear'] = s.left_unload
results['close_ok_after_clear'] = s.close_door()
results['served_all'] = (s.served == SEATS)

order = ['retained_on_arrival','release_blocked_door_closed','open_ok','release_after_open',
         'close_blocked_midunload','rv_waits_during_unload','area_clear_after_timer',
         'rv_left_after_clear','close_ok_after_clear','served_all']
for i, k in enumerate(order, 1):
    print("[" + str(i) + "] " + k + ": " + str(results[k]) + " -> " + ("PASS" if results[k] else "FAIL"))
print("ALL PASS" if all(results.values()) else "SOME FAILED")
