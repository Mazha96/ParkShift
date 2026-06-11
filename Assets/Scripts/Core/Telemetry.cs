using System;
using System.Collections.Generic;
using System.Text;

namespace ParkShift.Core
{
    /// <summary>In-memory playtest telemetry. The Unity layer flushes this to disk/console.</summary>
    public sealed class Telemetry
    {
        public readonly struct Entry
        {
            public readonly float SimTime;
            public readonly string Category;
            public readonly string Message;
            public Entry(float t, string cat, string msg) { SimTime = t; Category = cat; Message = msg; }
            public override string ToString() => $"[{SimTime,7:0.0}s] {Category,-10} {Message}";
        }

        readonly List<Entry> _entries = new List<Entry>();
        public IReadOnlyList<Entry> Entries => _entries;
        public event Action<Entry> OnEntry;

        public void Log(float simTime, string category, string message)
        {
            var e = new Entry(simTime, category, message);
            _entries.Add(e);
            OnEntry?.Invoke(e);
        }

        public string Dump()
        {
            var sb = new StringBuilder();
            foreach (var e in _entries) sb.AppendLine(e.ToString());
            return sb.ToString();
        }
    }
}
