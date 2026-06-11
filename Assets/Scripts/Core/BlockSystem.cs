using System;

namespace ParkShift.Core
{
    /// <summary>
    /// Classic block-zone safety logic: a train may only enter a block if it is unoccupied.
    /// Block 0 is the station. Trains advance 0 -> 1 -> ... -> N-1 -> 0.
    /// </summary>
    public sealed class BlockSystem
    {
        readonly int[] _occupant; // train id per block, -1 = clear
        public int BlockCount { get; }
        public event Action<int, int> OnBlockEntered; // (blockIndex, trainId)

        public BlockSystem(int blockCount)
        {
            if (blockCount < 2) throw new ArgumentException("Need at least station + one block");
            BlockCount = blockCount;
            _occupant = new int[blockCount];
            for (int i = 0; i < blockCount; i++) _occupant[i] = -1;
        }

        public int NextBlock(int block) => (block + 1) % BlockCount;
        public bool IsClear(int block) => _occupant[block] == -1;
        public int OccupantOf(int block) => _occupant[block];

        public bool TryEnter(int block, int trainId)
        {
            if (!IsClear(block)) return false;
            _occupant[block] = trainId;
            OnBlockEntered?.Invoke(block, trainId);
            return true;
        }

        public void Leave(int block, int trainId)
        {
            if (_occupant[block] == trainId) _occupant[block] = -1;
        }

        public void ForcePlace(int block, int trainId) { _occupant[block] = trainId; }
    }
}
