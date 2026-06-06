using System;
using System.Collections.Generic;
using UnityEngine;

namespace RatcliffDefense.RadarDataExport
{
    /// <summary>
    /// Egocentric avionics state for the XRAY AVIONICS SUITE (single-pilot client).
    ///
    /// The team radar export answers "what does my faction know"; this answers
    /// "what is happening to *my* aircraft" — specifically the Radar Warning
    /// Receiver. The game raises <see cref="Aircraft.OnRadarWarning"/> as a
    /// ClientRpc every time a hostile radar paints us, carrying whether that radar
    /// actually got a return on us (<c>detected</c> — a real radar track, not a
    /// datalink track) and whether we are its locked target (<c>isTarget</c>).
    /// Those events are momentary, so we accumulate them per emitter with a
    /// time-out, mirroring the game's own RadarWarning HUD element.
    ///
    /// Pure main-thread: the RPC handler and the snapshot builder both run on the
    /// Unity main thread, so the dictionary needs no locking.
    /// </summary>
    internal sealed class AvionicsTracker
    {
        internal struct RwrContact
        {
            public Unit emitter;
            public float power;     // return signal strength (EstimateDetection)
            public bool detected;   // emitter's radar actually has a return on us
            public bool isTarget;   // we are this emitter's current locked target (STT)
            public bool fresh;      // first ping since the contact (dis)appeared
            public float last;      // Time.timeSinceLevelLoad of the most recent ping
        }

        // How long a painted contact lingers on the RWR after its last ping. Real
        // scopes hold a symbol a few seconds between sweeps; the game's own warning
        // icon lives 4s. We allow a touch longer so 10Hz exports never flicker.
        private float _timeout = 6f;

        private Aircraft _ac;
        private Action<Aircraft.OnRadarWarning> _handler;
        private readonly Dictionary<uint, RwrContact> _rwr = new Dictionary<uint, RwrContact>();
        private readonly List<uint> _expired = new List<uint>();

        public void SetTimeout(float seconds) { _timeout = Mathf.Max(1f, seconds); }

        /// <summary>(Re)bind to the local aircraft's RWR; call once per snapshot on
        /// the main thread. Cheap no-op while the aircraft is unchanged.</summary>
        public void Refresh(Aircraft self)
        {
            if (ReferenceEquals(self, _ac))
                return;

            if (_ac != null && _handler != null)
                _ac.onRadarWarning -= _handler;

            _rwr.Clear();
            _ac = self;

            if (_ac != null)
            {
                _handler = OnRadarWarning;
                _ac.onRadarWarning += _handler;
            }
            else
            {
                _handler = null;
            }
        }

        private void OnRadarWarning(Aircraft.OnRadarWarning w)
        {
            // We share the aircraft's onRadarWarning event with the game's own
            // RWR HUD. A subscriber that throws would break the multicast chain
            // and could stop the in-game warning updating, so this handler is
            // read-only and never lets an exception escape.
            try
            {
                if (w.emitter == null)
                    return;

                uint id = w.emitter.persistentID.Id;
                bool known = _rwr.ContainsKey(id);
                _rwr[id] = new RwrContact
                {
                    emitter = w.emitter,
                    power = w.power,
                    detected = w.detected,
                    isTarget = w.isTarget,
                    fresh = !known,
                    last = Time.timeSinceLevelLoad,
                };
            }
            catch
            {
                // Never disturb the game's own RWR handlers.
            }
        }

        /// <summary>Live RWR contacts, oldest stale entries pruned. The returned
        /// list is reused — copy out before the next call.</summary>
        public IReadOnlyList<RwrContact> Current(float now, List<RwrContact> into)
        {
            into.Clear();
            _expired.Clear();
            foreach (var pair in _rwr)
            {
                RwrContact c = pair.Value;
                if (c.emitter == null || c.emitter.disabled || now - c.last > _timeout)
                {
                    _expired.Add(pair.Key);
                    continue;
                }
                into.Add(c);
            }
            for (int i = 0; i < _expired.Count; i++)
                _rwr.Remove(_expired[i]);
            return into;
        }
    }
}
