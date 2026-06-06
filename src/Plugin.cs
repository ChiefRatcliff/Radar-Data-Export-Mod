using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace RatcliffDefense.RadarDataExport
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RadarDataPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.ratcliffdefense.radardataexport";
        public const string PluginName = "Radar Data Export Mod by Ratcliff Defense Systems";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;

        private ConfigEntry<bool> _enabled;
        private ConfigEntry<string> _bindAddress;
        private ConfigEntry<int> _port;
        private ConfigEntry<float> _updateHz;
        private ConfigEntry<bool> _includeFriendlies;
        private ConfigEntry<bool> _includeBogeyNames;
        private ConfigEntry<bool> _onlyMapVisible;
        private ConfigEntry<bool> _includeGrid;

        private TcpRadarServer _server;
        private RadarExporter _exporter;
        private float _interval;
        private float _accumulator;
        private bool _buildErrorLogged;

        private void Awake()
        {
            Log = Logger;

            _enabled = Config.Bind("General", "Enabled", true,
                "Master switch. When false the export server never starts.");
            _bindAddress = Config.Bind("Network", "BindAddress", "127.0.0.1",
                "Local IP to bind the TCP listener to. Use 127.0.0.1 for same-machine clients, or 0.0.0.0 to allow other machines on your LAN.");
            _port = Config.Bind("Network", "Port", 7070,
                "TCP port that GCI / AWACS / early-warning clients connect to.");
            _updateHz = Config.Bind("Network", "UpdateRateHz", 10f,
                new ConfigDescription("Radar snapshots broadcast per second.",
                    new AcceptableValueRange<float>(1f, 60f)));
            _includeFriendlies = Config.Bind("Data", "IncludeFriendlies", true,
                "Include the local player's own team units (always known, full fidelity).");
            _includeBogeyNames = Config.Bind("Data", "IncludeBogeyNames", true,
                "Include each contact's NATO-style bogey name in addition to its type name.");
            _onlyMapVisible = Config.Bind("Data", "OnlyMapVisibleUnits", true,
                "Only export units that have a map icon, matching exactly what shows on the player's in-game map.");
            _includeGrid = Config.Bind("Data", "IncludeGridReference", true,
                "Include each contact's in-game map grid reference (e.g. \"Cf37\"), exactly as shown on the player's map.");

            if (!_enabled.Value)
            {
                Log.LogInfo("Disabled via config; export server not started.");
                return;
            }

            _interval = 1f / Mathf.Max(1f, _updateHz.Value);
            _exporter = new RadarExporter(_includeFriendlies, _includeBogeyNames, _onlyMapVisible, _includeGrid);

            _server = new TcpRadarServer(_bindAddress.Value, _port.Value, Log);
            if (!_server.Start())
            {
                _server = null;
                return;
            }

            Log.LogInfo($"Radar export listening on {_bindAddress.Value}:{_port.Value} @ {_updateHz.Value:0.#} Hz.");
        }

        private void Update()
        {
            if (_server == null)
                return;

            _accumulator += Time.unscaledDeltaTime;
            if (_accumulator < _interval)
                return;
            _accumulator = 0f;

            // No consumers connected: do zero work building snapshots.
            if (!_server.HasClients)
                return;

            // Never let a snapshot-build error (e.g. a game-version API change, an
            // unexpected null) escape into Unity's loop. Log the first occurrence
            // of an error episode and suppress repeats so a persistent fault can't
            // spam the console every frame.
            try
            {
                string snapshot = _exporter.BuildSnapshotJson();
                if (snapshot != null)
                    _server.Broadcast(snapshot);
                _buildErrorLogged = false;
            }
            catch (System.Exception e)
            {
                if (!_buildErrorLogged)
                {
                    Log.LogError($"Snapshot build error (further repeats suppressed until it clears): {e}");
                    _buildErrorLogged = true;
                }
            }
        }

        private void OnDestroy()
        {
            _exporter?.Detach();       // unsubscribe the RWR event from the local aircraft
            _server?.Stop();
            _server = null;
        }
    }
}
