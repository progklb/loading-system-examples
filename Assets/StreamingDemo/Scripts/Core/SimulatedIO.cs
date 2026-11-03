using System;
using System.Collections.Generic;
using UnityEngine;

namespace StreamingDemo
{
    /// <summary>Lifecycle of a single read request.</summary>
    public enum RequestState
    {
        Queued,     // waiting in the priority queue
        Seeking,    // in flight, paying fixed latency (disc seek / HTTP round trip)
        Reading,    // in flight, transferring bytes (shares bandwidth with other readers)
        Done,
        Cancelled
    }

    /// <summary>
    /// One asynchronous "read this asset from storage" request.
    /// Every streaming system in the demo is built from these.
    /// </summary>
    public sealed class LoadRequest
    {
        public readonly string Label;
        public readonly float SizeMB;
        public readonly object Owner;

        /// <summary>LOWER value = MORE urgent. Systems update this every frame.</summary>
        public float Priority;

        public RequestState State { get; internal set; } = RequestState.Queued;
        public float ReadMB { get; internal set; }
        public float SeekRemaining { get; internal set; }
        public float QueuedAt { get; internal set; }
        public float StartedAt { get; internal set; }
        public float CompletedAt { get; internal set; }

        internal Action<LoadRequest> OnComplete;
        internal long Sequence; // tie-breaker so equal priorities stay FIFO

        public float Progress => SizeMB <= 0f ? 1f : Mathf.Clamp01(ReadMB / SizeMB);
        public bool IsInFlight => State == RequestState.Seeking || State == RequestState.Reading;
        public bool IsPending => State == RequestState.Queued || IsInFlight;

        internal LoadRequest(string label, float sizeMB, float priority, object owner)
        {
            Label = label;
            SizeMB = sizeMB;
            Priority = priority;
            Owner = owner;
        }
    }

    /// <summary>
    /// A deliberately simple model of a storage device (DVD, HDD, SSD, network...).
    ///
    /// The point of the model is to make the REAL constraint of streaming visible:
    /// you can always decide what you WANT loaded instantly, but bytes arrive at
    /// a fixed rate. Every strategy in this demo is really an answer to the
    /// question "in which order should I spend my limited bandwidth?".
    ///
    /// Model:
    ///  * Requests wait in a priority queue (lowest Priority value first).
    ///  * Up to <see cref="MaxInFlight"/> requests are serviced at once.
    ///  * Each in-flight request first pays <see cref="SeekMs"/> of latency,
    ///    then reads its bytes. Bandwidth is shared equally between readers.
    ///  * Completion fires a callback; the caller decides what "resident" means.
    /// </summary>
    public sealed class SimulatedIO
    {
        public string Name;
        public float BandwidthMBps = 20f;
        public float SeekMs = 10f;
        public int MaxInFlight = 2;

        /// <summary>Units shown in the HUD. "MB" for disks, "chunks" for a CPU worker pool, etc.</summary>
        public string Unit = "MB";

        readonly List<LoadRequest> _queued = new List<LoadRequest>();
        readonly List<LoadRequest> _inFlight = new List<LoadRequest>();
        readonly List<LoadRequest> _scratch = new List<LoadRequest>();
        long _sequence;
        float _readSinceSample;

        public IReadOnlyList<LoadRequest> Queued => _queued;
        public IReadOnlyList<LoadRequest> InFlight => _inFlight;
        public int PendingCount => _queued.Count + _inFlight.Count;

        public int TotalCompleted { get; private set; }
        public float TotalReadMB { get; private set; }

        /// <summary>True while anything is being read (useful for "drive light" style UI).</summary>
        public bool Busy => _inFlight.Count > 0;

        public SimulatedIO(string name) { Name = name; }

        /// <summary>Queue a read. The callback runs on the main thread when the last byte arrives.</summary>
        public LoadRequest Enqueue(string label, float sizeMB, float priority, Action<LoadRequest> onComplete, object owner = null)
        {
            var r = new LoadRequest(label, sizeMB, priority, owner)
            {
                OnComplete = onComplete,
                QueuedAt = Time.time,
                Sequence = _sequence++
            };
            _queued.Add(r);
            return r;
        }

        /// <summary>
        /// Cancel a request. Queued requests vanish for free; in-flight ones waste
        /// whatever bandwidth they already used, which is why good streamers avoid
        /// thrashing (see the hysteresis toggles).
        /// </summary>
        public void Cancel(LoadRequest r)
        {
            if (r == null || !r.IsPending) return;
            r.State = RequestState.Cancelled;
            _queued.Remove(r);
            _inFlight.Remove(r);
        }

        public void CancelAll()
        {
            foreach (var r in _queued) r.State = RequestState.Cancelled;
            foreach (var r in _inFlight) r.State = RequestState.Cancelled;
            _queued.Clear();
            _inFlight.Clear();
        }

        public void ResetStats()
        {
            TotalCompleted = 0;
            TotalReadMB = 0;
            _readSinceSample = 0;
        }

        /// <summary>Amount read since last call. Used by the metrics graph.</summary>
        public float ConsumeReadSinceSample()
        {
            float v = _readSinceSample;
            _readSinceSample = 0;
            return v;
        }

        /// <summary>Advance the simulation. Called once per frame by the app.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;

            // 1) Start the most urgent queued requests while we have free channels.
            if (_queued.Count > 0 && _inFlight.Count < MaxInFlight)
            {
                _queued.Sort(CompareRequests);
                while (_inFlight.Count < MaxInFlight && _queued.Count > 0)
                {
                    var r = _queued[0];
                    _queued.RemoveAt(0);
                    r.State = RequestState.Seeking;
                    r.SeekRemaining = SeekMs / 1000f;
                    r.StartedAt = Time.time;
                    _inFlight.Add(r);
                }
            }

            if (_inFlight.Count == 0) return;

            // 2) Seeking requests burn latency. Readers share the bandwidth.
            int readers = 0;
            foreach (var r in _inFlight)
            {
                if (r.State == RequestState.Seeking)
                {
                    r.SeekRemaining -= dt;
                    if (r.SeekRemaining <= 0f) r.State = RequestState.Reading;
                }
                if (r.State == RequestState.Reading) readers++;
            }

            if (readers == 0) return;

            float share = BandwidthMBps * dt / readers;
            _scratch.Clear();
            foreach (var r in _inFlight)
            {
                if (r.State != RequestState.Reading) continue;
                float take = Mathf.Min(share, r.SizeMB - r.ReadMB);
                r.ReadMB += take;
                _readSinceSample += take;
                TotalReadMB += take;
                if (r.ReadMB >= r.SizeMB - 1e-5f) _scratch.Add(r);
            }

            // 3) Complete. Remove first so callbacks may enqueue new work safely.
            foreach (var r in _scratch)
            {
                _inFlight.Remove(r);
                r.State = RequestState.Done;
                r.CompletedAt = Time.time;
                TotalCompleted++;
            }
            foreach (var r in _scratch) r.OnComplete?.Invoke(r);
        }

        static int CompareRequests(LoadRequest a, LoadRequest b)
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Sequence.CompareTo(b.Sequence);
        }
    }

    /// <summary>
    /// Tracks how much is resident in memory against a fixed budget.
    /// Kept separate from <see cref="SimulatedIO"/> on purpose: the decision of
    /// WHEN data becomes resident (and when it is evicted) belongs to the
    /// streaming strategy, not the disk.
    /// </summary>
    public sealed class MemoryBudget
    {
        public float BudgetMB = 256f;
        public float UsedMB { get; private set; }
        public float PeakMB { get; private set; }
        public bool OverBudget => UsedMB > BudgetMB + 1e-3f;
        public float Fraction => BudgetMB <= 0 ? 0 : UsedMB / BudgetMB;

        public void Allocate(float mb)
        {
            UsedMB += mb;
            PeakMB = Mathf.Max(PeakMB, UsedMB);
        }

        public void Free(float mb) => UsedMB = Mathf.Max(0f, UsedMB - mb);

        public void Reset()
        {
            UsedMB = 0;
            PeakMB = 0;
        }
    }
}
