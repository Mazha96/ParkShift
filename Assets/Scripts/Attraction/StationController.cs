using UnityEngine;
using ParkShift.Core;

namespace ParkShift.Attraction
{
    /// <summary>
    /// Thin, read-only view over the sim's two-position station (when SimConfig.SeparateLoadUnload
    /// is on): which RV is at Unload vs Load, whether each side is occupied, gate state, and whether
    /// dispatch is currently allowed. It does NOT hold its own copy of that state and never mutates
    /// the sim — the existing OpVerb flow still drives all actions. It also carries the station marker
    /// transforms so other systems (and the inspector) can reference Unload/Load stop points and the
    /// guest exit/entry points without hard-coding positions.
    /// </summary>
    public class StationController : MonoBehaviour
    {
        [Header("Stop points (RV parking)")]
        public Transform UnloadStopPoint;
        public Transform LoadStopPoint;

        [Header("Guest path markers")]
        public Transform[] UnloadGuestExitPoints;
        public Transform[] LoadGuestEntryPoints;

        AttractionSim Sim => AttractionManager.Instance != null ? AttractionManager.Instance.Sim : null;

        /// <summary>True when the station is configured as two blocks (Unload + Load).</summary>
        public bool SplitStation => Sim != null && Sim.Config.SeparateLoadUnload;

        /// <summary>The RV dropping riders at Unload (block 0), or null.</summary>
        public TrainSim UnloadRV => Sim != null ? Sim.UnloadingTrain() : null;
        /// <summary>The RV boarding / waiting to dispatch at Load, or null.</summary>
        public TrainSim LoadRV => Sim != null ? Sim.PlatformTrain() : null;

        public bool UnloadOccupied => Sim != null && !Sim.Blocks.IsClear(Sim.Config.UnloadBlock);
        public bool LoadOccupied   => Sim != null && !Sim.Blocks.IsClear(Sim.Config.LoadBlock);
        public bool GatesOpen      => Sim != null && Sim.Guests.GatesOpen;

        /// <summary>Mirrors AttractionSim.CanDispatch — true only when gates are closed, the Load RV is
        /// ready, the next section is clear and the ride is powered. (The sim is the single source of
        /// truth; this just surfaces it for HUD/FX.)</summary>
        public bool DispatchAllowed
        {
            get { string _; return Sim != null && Sim.CanDispatch(out _); }
        }

        public string DispatchBlockedReason
        {
            get { string why; if (Sim == null) return "No sim"; Sim.CanDispatch(out why); return why; }
        }
    }
}
