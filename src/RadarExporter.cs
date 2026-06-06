using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace RatcliffDefense.RadarDataExport
{
    /// <summary>
    /// Reads the local player's faction view and serializes one radar snapshot.
    /// MUST be called on the Unity main thread: it touches Unity objects.
    ///
    /// Fog of war is enforced by the game itself: enemy contacts come from
    /// FactionHQ.trackingDatabase, which the server only populates with units the
    /// faction has actually detected, then syncs to this client. We never read an
    /// undetected enemy's live transform.
    /// </summary>
    internal sealed class RadarExporter
    {
        private readonly ConfigEntry<bool> _includeFriendlies;
        private readonly ConfigEntry<bool> _includeBogey;
        private readonly ConfigEntry<bool> _onlyMapVisible;
        private readonly ConfigEntry<bool> _includeGrid;

        // Reused across snapshots to keep per-tick allocation low.
        private readonly StringBuilder _sb = new StringBuilder(16384);

        // Egocentric avionics (RWR / missile warning) for the XRAY suite.
        private readonly AvionicsTracker _avionics = new AvionicsTracker();
        private readonly List<AvionicsTracker.RwrContact> _rwrScratch = new List<AvionicsTracker.RwrContact>();

        public RadarExporter(ConfigEntry<bool> includeFriendlies, ConfigEntry<bool> includeBogey, ConfigEntry<bool> onlyMapVisible, ConfigEntry<bool> includeGrid)
        {
            _includeFriendlies = includeFriendlies;
            _includeBogey = includeBogey;
            _onlyMapVisible = onlyMapVisible;
            _includeGrid = includeGrid;
        }

        /// <summary>Unsubscribe the RWR hook from the local aircraft - called on
        /// plugin teardown so we never linger on a still-alive Aircraft.</summary>
        public void Detach()
        {
            _avionics.Refresh(null);
        }

        /// <returns>One newline-terminated JSON object, or null if not in a match.</returns>
        public string BuildSnapshotJson()
        {
            if (!GameManager.GetLocalHQ(out FactionHQ hq) || hq == null)
                return null;

            float now = Time.timeSinceLevelLoad;
            StringBuilder sb = _sb;
            sb.Clear();

            sb.Append("{\"v\":2");
            sb.Append(",\"t\":").Append(Num(now));
            sb.Append(",\"faction\":").Append(Str(hq.faction != null ? hq.faction.factionName : "Unknown"));

            uint selfId = 0u;
            Aircraft localAc = null;
            if (GameManager.GetLocalAircraft(out Aircraft self) && self != null && !self.disabled)
            {
                localAc = self;
                selfId = self.persistentID.Id;
            }
            sb.Append(",\"selfId\":").Append(selfId);

            // Keep the RWR bound to whatever aircraft we're flying (cheap no-op
            // while unchanged). Subscription persists between snapshots so we catch
            // every radar-warning RPC, not just those landing on a build tick.
            _avionics.Refresh(localAc);

            // Resolve the map's grid labeller once per snapshot; null when grid is
            // disabled or the map isn't available, in which case contacts omit grid.
            GridLabels grid = _includeGrid.Value ? ResolveGrid() : null;

            // Enemy / munition contacts, limited to what the team has detected.
            sb.Append(",\"hostile\":[");
            bool first = true;
            foreach (var pair in hq.trackingDatabase)
            {
                TrackingInfo info = pair.Value;
                if (!info.TryGetUnit(out Unit unit) || unit == null || unit.disabled)
                    continue;
                if (!PassesMapFilter(unit))
                    continue;

                bool live = info.Observed();
                float age = now - info.lastSpottedTime;
                GlobalPosition pos = info.GetPosition();

                if (!first) sb.Append(',');
                first = false;
                // Kinematics are only meaningful while the contact is actively
                // observed; for a stale track we emit the last known position only.
                WriteContact(sb, unit, pos, live, age, includeKinematics: live, friendly: false, grid: grid);
            }
            sb.Append(']');

            // Own team. Always fully known, so kinematics are always included.
            if (_includeFriendlies.Value)
            {
                sb.Append(",\"friendly\":[");
                first = true;
                var ids = hq.factionUnits;
                int count = ids.Count;
                for (int i = 0; i < count; i++)
                {
                    if (!UnitRegistry.TryGetUnit(ids[i], out Unit unit) || unit == null || unit.disabled)
                        continue;
                    if (!PassesMapFilter(unit))
                        continue;

                    if (!first) sb.Append(',');
                    first = false;
                    WriteContact(sb, unit, unit.GlobalPosition(), live: true, age: 0f, includeKinematics: true, friendly: true, grid: grid);
                }
                sb.Append(']');
            }

            // Egocentric avionics block — only meaningful while we're piloting.
            if (localAc != null)
                WriteAvionics(sb, localAc, now);

            sb.Append("}\n");
            return sb.ToString();
        }

        private bool PassesMapFilter(Unit unit)
        {
            if (!_onlyMapVisible.Value)
                return true;
            return unit.definition != null && unit.definition.mapIconSize > 0f;
        }

        // Egocentric avionics for the XRAY single-pilot client: radar on/off and
        // range, the Radar Warning Receiver picture (hostile radars painting us,
        // flagged by whether they actually detected/locked us), and incoming
        // missiles our warning system knows about. All bearings are compass
        // degrees (0 = North, 90 = East); the client rotates them nose-up.
        private void WriteAvionics(StringBuilder sb, Aircraft self, float now)
        {
            GlobalPosition sp = self.GlobalPosition();
            TargetDetector rdr = self.radar;
            bool radarOn = rdr != null && rdr.activated;
            float radarRange = rdr != null ? rdr.GetRadarRange() : 0f;

            sb.Append(",\"avionics\":{");
            sb.Append("\"radarOn\":").Append(radarOn ? "true" : "false");
            sb.Append(",\"radarRange\":").Append(Num(radarRange));

            // Own-radar contacts: what THIS aircraft's own radar/sensors detect
            // directly, with NO datalink. detectedTargets is populated locally for
            // the player's own aircraft (TargetDetector runs its scan when
            // IsLocalAircraft), so this is the radar picture the pilot truly owns -
            // a strict subset of the team's datalink track database.
            sb.Append(",\"rcontacts\":[");
            var dt = rdr != null ? rdr.detectedTargets : null;
            if (dt != null)
            {
                bool firstC = true;
                for (int i = 0; i < dt.Count; i++)
                {
                    Unit u = dt[i];
                    if (u == null || u.disabled)
                        continue;
                    GlobalPosition up = u.GlobalPosition();
                    if (!firstC) sb.Append(',');
                    firstC = false;
                    sb.Append("{\"id\":").Append(u.persistentID.Id);
                    sb.Append(",\"name\":").Append(Str(DisplayName(u)));
                    sb.Append(",\"cls\":").Append(Str(Category(u)));
                    sb.Append(",\"x\":").Append(Num(up.x));
                    sb.Append(",\"y\":").Append(Num(up.y));
                    sb.Append(",\"z\":").Append(Num(up.z));
                    sb.Append('}');
                }
            }
            sb.Append(']');

            // RWR: hostile radars currently illuminating us. det = the emitter's
            // radar has a real return on us (a track of its own, not a datalink
            // hand-off); lock = we are its designated target (single-target track).
            sb.Append(",\"rwr\":[");
            var list = _avionics.Current(now, _rwrScratch);
            bool first = true;
            for (int i = 0; i < list.Count; i++)
            {
                AvionicsTracker.RwrContact c = list[i];
                Unit em = c.emitter;
                if (em == null || em.disabled)
                    continue;
                GlobalPosition ep = em.GlobalPosition();
                float dx = ep.x - sp.x, dz = ep.z - sp.z;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"id\":").Append(em.persistentID.Id);
                sb.Append(",\"name\":").Append(Str(DisplayName(em)));
                if (_includeBogey.Value && em.definition != null && !string.IsNullOrEmpty(em.definition.bogeyName))
                    sb.Append(",\"bogey\":").Append(Str(em.definition.bogeyName));
                sb.Append(",\"cls\":").Append(Str(Category(em)));
                sb.Append(",\"brg\":").Append(Num(Bearing(dx, dz)));
                sb.Append(",\"rng\":").Append(Num(Mathf.Sqrt(dx * dx + dz * dz)));
                sb.Append(",\"pwr\":").Append(Num(c.power));
                sb.Append(",\"det\":").Append(c.detected ? "true" : "false");
                sb.Append(",\"lock\":").Append(c.isTarget ? "true" : "false");
                if (c.fresh) sb.Append(",\"fresh\":true");
                sb.Append(",\"age\":").Append(Num(now - c.last));
                sb.Append('}');
            }
            sb.Append(']');

            // Missile launch warnings — incoming missiles the MWS has acquired.
            sb.Append(",\"mw\":[");
            MissileWarning mws = self.GetMissileWarningSystem();
            if (mws != null && mws.knownMissiles != null)
            {
                first = true;
                var km = mws.knownMissiles;
                for (int i = 0; i < km.Count; i++)
                {
                    Missile m = km[i];
                    if (m == null || m.disabled)
                        continue;
                    GlobalPosition mp = m.GlobalPosition();
                    float dx = mp.x - sp.x, dz = mp.z - sp.z;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(m.persistentID.Id);
                    sb.Append(",\"name\":").Append(Str(DisplayName(m)));
                    sb.Append(",\"brg\":").Append(Num(Bearing(dx, dz)));
                    sb.Append(",\"rng\":").Append(Num(Mathf.Sqrt(dx * dx + dz * dz)));
                    // Seeker classification so the RWR can flag active radar-guided
                    // missiles (ARH/SARH) distinctly from IR/optical threats.
                    string mseeker = null;
                    try { mseeker = m.GetSeekerType(); } catch { mseeker = null; }
                    if (!string.IsNullOrEmpty(mseeker))
                        sb.Append(",\"seeker\":").Append(Str(mseeker));
                    bool radarGuided = IsRadarSeeker(mseeker);
                    sb.Append(",\"radar\":").Append(radarGuided ? "true" : "false");
                    sb.Append(",\"active\":").Append((radarGuided && IsActiveSeekerMode(m.seekerMode)) ? "true" : "false");
                    sb.Append('}');
                }
            }
            sb.Append(']');

            sb.Append('}');
        }

        // Radar-guided seeker: ARH (active) or SARH (semi-active) radar homing.
        private static bool IsRadarSeeker(string seeker)
        {
            if (string.IsNullOrEmpty(seeker))
                return false;
            return seeker.IndexOf("ARH", System.StringComparison.OrdinalIgnoreCase) >= 0
                || seeker.IndexOf("Radar", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // The missile's own seeker radar is live (gone "pitbull"), not midcourse.
        private static bool IsActiveSeekerMode(Missile.SeekerMode mode)
        {
            return mode == Missile.SeekerMode.activeSearch || mode == Missile.SeekerMode.activeLock;
        }

        // Compass bearing from an East/North delta: 0 = North (+z), 90 = East (+x).
        private static float Bearing(float dEast, float dNorth)
        {
            float h = Mathf.Atan2(dEast, dNorth) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }

        private void WriteContact(StringBuilder sb, Unit unit, GlobalPosition pos, bool live, float age, bool includeKinematics, bool friendly, GridLabels grid)
        {
            sb.Append("{\"id\":").Append(unit.persistentID.Id);
            sb.Append(",\"name\":").Append(Str(DisplayName(unit)));
            if (_includeBogey.Value && unit.definition != null && !string.IsNullOrEmpty(unit.definition.bogeyName))
                sb.Append(",\"bogey\":").Append(Str(unit.definition.bogeyName));
            sb.Append(",\"cls\":").Append(Str(Category(unit)));
            sb.Append(",\"x\":").Append(Num(pos.x));
            sb.Append(",\"y\":").Append(Num(pos.y));
            sb.Append(",\"z\":").Append(Num(pos.z));
            if (grid != null)
            {
                string g = GridRef(grid, pos);
                if (!string.IsNullOrEmpty(g))
                    sb.Append(",\"grid\":").Append(Str(g));
            }
            if (includeKinematics)
            {
                sb.Append(",\"hdg\":").Append(Num(Heading(unit)));
                sb.Append(",\"spd\":").Append(Num(unit.speed));
            }

            // Munition intelligence: seeker type, guidance phase, target, owner.
            // Gated on observation so we never emit stale/undetected guidance.
            if (includeKinematics && unit is Missile missile)
                WriteMunition(sb, missile);

            // Weapon loadout for own-team aircraft only (fog of war: never enemies).
            if (friendly && unit is Aircraft aircraft)
                WriteWeapons(sb, aircraft);

            sb.Append(",\"live\":").Append(live ? "true" : "false");
            sb.Append(",\"age\":").Append(Num(age));
            sb.Append('}');
        }

        // Seeker type ("ARH","SARH","IR","ARAD","INS","Optical","Laser"), guidance
        // phase, locked target, and firing platform.
        private static void WriteMunition(StringBuilder sb, Missile m)
        {
            string seeker = null;
            try { seeker = m.GetSeekerType(); } catch { seeker = null; }
            if (!string.IsNullOrEmpty(seeker))
                sb.Append(",\"seeker\":").Append(Str(seeker));
            sb.Append(",\"smode\":").Append(Str(SeekerModeStr(m.seekerMode)));
            PersistentID tid = m.targetID;
            if (tid.IsValid)
                sb.Append(",\"tgt\":").Append(tid.Id);
            if (m.ownerID.IsValid)
                sb.Append(",\"own\":").Append(m.ownerID.Id);
        }

        // passive = midcourse / datalink; search/lock = own radar active (pitbull).
        private static string SeekerModeStr(Missile.SeekerMode mode)
        {
            switch (mode)
            {
                case Missile.SeekerMode.activeLock: return "lock";
                case Missile.SeekerMode.activeSearch: return "search";
                default: return "passive";
            }
        }

        // Per-station live weapons (shortName + remaining ammo); falls back to the
        // synced loadout when the live stations aren't populated on this client.
        private static void WriteWeapons(StringBuilder sb, Aircraft ac)
        {
            var stations = ac.weaponStations;
            if (stations != null && stations.Count > 0)
            {
                sb.Append(",\"wpn\":[");
                bool first = true;
                for (int i = 0; i < stations.Count; i++)
                {
                    WeaponStation ws = stations[i];
                    if (ws == null || ws.WeaponInfo == null) continue;
                    string n = WeaponName(ws.WeaponInfo);
                    if (string.IsNullOrEmpty(n)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"s\":").Append(i).Append(",\"n\":").Append(Str(n)).Append(",\"a\":").Append(ws.Ammo).Append('}');
                }
                sb.Append(']');
                WeaponManager wm = ac.weaponManager;
                if (wm != null && wm.currentWeaponStation != null)
                    sb.Append(",\"wsel\":").Append((int)wm.currentWeaponStation.Number);
                return;
            }

            var lo = ac.loadout;
            if (lo == null || lo.weapons == null || lo.weapons.Count == 0)
                return;
            sb.Append(",\"wpn\":[");
            bool f = true;
            foreach (WeaponMount mount in lo.weapons)
            {
                if (mount == null || mount.info == null) continue;
                string n = WeaponName(mount.info);
                if (string.IsNullOrEmpty(n)) continue;
                if (!f) sb.Append(',');
                f = false;
                sb.Append("{\"n\":").Append(Str(n)).Append(",\"a\":").Append(mount.ammo).Append('}');
            }
            sb.Append(']');
        }

        private static string WeaponName(WeaponInfo info)
        {
            if (info == null) return null;
            return !string.IsNullOrEmpty(info.shortName) ? info.shortName : info.weaponName;
        }

        // The in-game map's grid labeller. Null when there's no DynamicMap in the
        // scene (use != null, not ?., so Unity's destroyed-object check applies).
        private static GridLabels ResolveGrid()
        {
            DynamicMap dm = SceneSingleton<DynamicMap>.i;
            return (dm != null && dm.gridLabels != null) ? dm.gridLabels : null;
        }

        // The exact grid reference the player sees (e.g. "Cf37"), computed by the
        // game itself; empty when the contact is off the labelled map. The catch
        // covers the brief window before the map finishes SetupGrid on a new level.
        private static string GridRef(GridLabels grid, GlobalPosition pos)
        {
            try { return grid.GetGridPosition(pos); }
            catch { return null; }
        }

        private static string DisplayName(Unit unit)
        {
            if (unit.definition != null && !string.IsNullOrEmpty(unit.definition.unitName))
                return unit.definition.unitName;
            return string.IsNullOrEmpty(unit.unitName) ? "Unknown" : unit.unitName;
        }

        private static string Category(Unit unit)
        {
            if (unit is Aircraft) return "Aircraft";
            if (unit is Missile) return "Missile";
            if (unit is Ship) return "Ship";
            if (unit is GroundVehicle) return "Ground";
            if (unit is Building) return "Building";
            return "Unknown";
        }

        // Compass heading in degrees: 0 = North (+Z), 90 = East (+X).
        private static float Heading(Unit unit)
        {
            Vector3 f = unit.transform.forward;
            float h = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            return h;
        }

        private static string Num(float v)
        {
            // NaN / Infinity would serialize as bare tokens that are invalid JSON
            // and break every client parsing that snapshot line - clamp to 0.
            if (float.IsNaN(v) || float.IsInfinity(v))
                return "0";
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Str(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
