using UnityEngine;

namespace StreamingDemo
{
    /// <summary>
    /// Fixed-size ring buffer of samples for the HUD sparklines.
    /// </summary>
    public sealed class RingSeries
    {
        readonly float[] _data;
        int _head;
        public int Count { get; private set; }
        public int Capacity => _data.Length;

        public RingSeries(int capacity) { _data = new float[capacity]; }

        public void Push(float v)
        {
            _data[_head] = v;
            _head = (_head + 1) % _data.Length;
            if (Count < _data.Length) Count++;
        }

        /// <summary>i = 0 is the oldest sample.</summary>
        public float this[int i] => _data[(_head - Count + i + _data.Length * 2) % _data.Length];

        public float Max()
        {
            float m = 0;
            for (int i = 0; i < Count; i++) m = Mathf.Max(m, this[i]);
            return m;
        }

        public void Clear() { _head = 0; Count = 0; }
    }

    /// <summary>
    /// Samples the shared disk + memory budget ten times a second so every mode
    /// gets the same throughput / memory graphs for free.
    /// </summary>
    public sealed class Metrics
    {
        public const float SampleInterval = 0.1f;

        public readonly RingSeries Throughput = new RingSeries(200); // MB/s
        public readonly RingSeries Memory = new RingSeries(200);     // MB resident
        public readonly RingSeries QueueDepth = new RingSeries(200); // pending requests

        float _accum;

        public void Tick(float dt, SimulatedIO disk, MemoryBudget memory)
        {
            _accum += dt;
            if (_accum < SampleInterval) return;
            float read = disk.ConsumeReadSinceSample();
            Throughput.Push(read / _accum);
            Memory.Push(memory.UsedMB);
            QueueDepth.Push(disk.PendingCount);
            _accum = 0;
        }

        public void Reset()
        {
            Throughput.Clear();
            Memory.Clear();
            QueueDepth.Clear();
            _accum = 0;
        }
    }
}
