using System;
using System.Collections.Generic;
using XPlaneConnector;

namespace MobiFlight.xplane
{
    public class XplaneCache : XplaneCacheInterface
    {
        public event EventHandler Closed;
        public event EventHandler Connected;
        public event EventHandler ConnectionLost;
        public event EventHandler OnUpdateFrequencyPerSecondChanged;
        public event EventHandler<string> AircraftChanged;

        /// <summary>
        /// How long X-Plane may stay silent before we consider the connection dead.
        /// </summary>
        /// <remarks>
        /// The heartbeat dataref is requested at 1 Hz, so this allows for a generous amount of
        /// dropped UDP datagrams before giving up. This matters mostly for remote connections
        /// where a sleeping machine, a dropped Wi-Fi link or a closed sim cannot be detected by
        /// looking at the local process list.
        /// </remarks>
        internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Dataref used as heartbeat. It keeps counting up even while the sim is paused or sitting
        /// in a menu, which makes it a reliable indicator that X-Plane is still talking to us.
        /// </summary>
        private const string HeartbeatDataRef = "sim/time/total_running_time_sec";

        private bool _connected = false;
        private int _updateFrequencyPerSecond = 10;
        private string _detectedAircraft = string.Empty;
        private XplaneConnectionSettings _settings = new XplaneConnectionSettings();

        /// <summary>
        /// Timestamp of the most recent datagram received from X-Plane.
        /// </summary>
        private DateTime _lastDataReceived = DateTime.MinValue;

        private readonly object _connectorLock = new object();

        public int UpdateFrequencyPerSecond
        {
            get { return _updateFrequencyPerSecond; }
            set
            {
                if (_updateFrequencyPerSecond == value) return;
                _updateFrequencyPerSecond = value;
                OnUpdateFrequencyPerSecondChanged?.Invoke(value, new EventArgs());
            }
        }

        /// <summary>
        /// The endpoint MobiFlight connects to. Changing it while connected drops the current
        /// connection so the new endpoint is picked up on the next connect attempt.
        /// </summary>
        public XplaneConnectionSettings Settings
        {
            get { return _settings; }
            set
            {
                var newSettings = value ?? new XplaneConnectionSettings();
                if (_settings.Equals(newSettings)) return;

                Log.Instance.log($"X-Plane connection settings changed to {newSettings}.", LogSeverity.Info);
                _settings = newSettings;

                // Force a rebuild of the connector so the new endpoint takes effect.
                ResetConnector();
            }
        }

        XPlaneConnector.XPlaneConnector Connector = null;

        Dictionary<String, DataRefElement> SubscribedDataRefs = new Dictionary<String, DataRefElement>();

        public XplaneCache()
        {
            Connected += (s, e) =>
            {
                // As soon as we get connected
                // we want to check for the aircraft name,
                // so we can trigger the AircraftChanged event correctly
                CheckForAircraftName();
            };
        }

        public bool Connect()
        {
            lock (_connectorLock)
            {
                if (Connector == null)
                {
                    if (!_settings.TryResolveAddress(out var address, out var error))
                    {
                        Log.Instance.log($"Cannot connect to X-Plane: {error}", LogSeverity.Error);
                        return false;
                    }

                    try
                    {
                        Connector = new XPlaneConnector.XPlaneConnector(address.ToString(), _settings.Port);
                    }
                    catch (Exception ex)
                    {
                        Log.Instance.log($"Cannot connect to X-Plane at {_settings}: {ex.Message}", LogSeverity.Error);
                        return false;
                    }

                    Log.Instance.log($"Connecting to X-Plane at {address}:{_settings.Port}.", LogSeverity.Info);

                    Connector.OnLog += (m) =>
                    {
                        // Log.Instance.log(m, LogSeverity.Debug);
                    };

                    OnUpdateFrequencyPerSecondChanged += (v, e) =>
                    {
                        Log.Instance.log($"update frequency changed: {v} per second.", LogSeverity.Debug);
                        UnsubscribeAll();
                    };
                }

                WaitForConnection();
                return _connected;
            }
        }

        /// <summary>
        /// This method will raise a Connected event as soon as we detect that we are receiving values from the sim.
        /// The method subscribes to a DatRef which is present for all aircraft and changes constantly.
        /// </summary>
        private void WaitForConnection()
        {
            var dataRefTime = new DataRefElement() { DataRef = HeartbeatDataRef, Frequency = 1, Value = 0 };

            try
            {
                Connector.Start();
            }
            catch (Exception ex)
            {
                // Starting opens the UDP sockets, which can fail if the port is already taken.
                Log.Instance.log($"Could not start the X-Plane connection: {ex.Message}", LogSeverity.Error);
                ResetConnector();
                return;
            }

            Connector.Unsubscribe(dataRefTime.DataRef);
            Connector.Subscribe(dataRefTime, 1, (e, v) =>
            {
                // Track this before the early return below, otherwise the timestamp would only ever
                // be set once and the connection watchdog could never see the ongoing heartbeat.
                _lastDataReceived = DateTime.UtcNow;

#if DEBUG
                Log.Instance.log($"{HeartbeatDataRef} = {v}", LogSeverity.Debug);
#endif
                if (_connected) return;

                _connected = true;
                Connected?.Invoke(this, new EventArgs());
            });
        }

        /// <summary>
        /// Detects a connection that went away without us being told about it.
        /// </summary>
        /// <remarks>
        /// For a local sim MobiFlight notices a closed X-Plane because the process disappears.
        /// A remote sim gives us no such signal, so we fall back to watching the heartbeat dataref.
        /// Call this periodically (the auto connect timer does).
        /// </remarks>
        public void CheckConnectionStatus()
        {
            if (!_connected) return;
            if (DateTime.UtcNow - _lastDataReceived < ConnectionTimeout) return;

            Log.Instance.log(
                $"No data received from X-Plane at {_settings} for {ConnectionTimeout.TotalSeconds} seconds. Connection lost.",
                LogSeverity.Warn);

            _connected = false;
            _detectedAircraft = string.Empty;
            AircraftChanged?.Invoke(this, _detectedAircraft);

            // Drop the connector so the next connect attempt starts from a clean state.
            ResetConnector();

            ConnectionLost?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Tears down the current connector and forgets all subscriptions.
        /// </summary>
        private void ResetConnector()
        {
            lock (_connectorLock)
            {
                SubscribedDataRefs.Clear();
                _lastDataReceived = DateTime.MinValue;

                if (Connector == null) return;

                try
                {
                    Connector.Stop();
                }
                catch (Exception ex)
                {
                    Log.Instance.log($"Error while stopping the X-Plane connection: {ex.Message}", LogSeverity.Debug);
                }
                finally
                {
                    Connector = null;
                    _connected = false;
                }
            }
        }

        /// <summary>
        /// Updates the subscription to the aircraft name data reference and notifies listeners when the aircraft
        /// changes.
        /// </summary>
        /// <remarks>This method itself does not reliably detect the name change because of a flaw in the libary used.
        /// We basically resubscribe to the datarefs which will provide us with the correct aircraft name reliably.
        /// This method is called when CheckForAircraftName detects a change in the first or third character of the aircraft name.
        /// </remarks>
        private void UpdateAircraftSubscription()
        {
            if (Connector == null) return;

            StringDataRefElement datarefAircraftName = new StringDataRefElement
            {
                DataRef = "sim/aircraft/view/acf_ui_name",
                Frequency = 1,
                Value = string.Empty,
                StringLenght = 64
            };

            Connector.Unsubscribe(datarefAircraftName.DataRef);
            Connector.Subscribe(datarefAircraftName, 1, (e1, v1) =>
            {
                if (_detectedAircraft == v1) return;
                _detectedAircraft = v1;
                AircraftChanged?.Invoke(this, _detectedAircraft);
            });
        }

        /// <summary>
        /// This method probes two characters of the aircraft name for change and is used in conjunction with the UpdateAircraftSubscription method.
        /// </summary>
        /// <remarks>It is a workkaround because the StringDataRefElement does not trigger
        /// the change reliably. Subscribing only two characters does work reliably.
        /// If the old aircraft and the new aircraft have the same characters at the position 0 and 2, \
        /// then the change will not be detected. But in most cases this should work fine.
        /// </remarks>
        private void CheckForAircraftName()
        {
            if (!_connected) return;
            if (Connector == null) return;

            _detectedAircraft = string.Empty;

            // Note: the connector is already running at this point, it was started in
            // WaitForConnection. Starting it again would open a second pair of UDP sockets and
            // leak the first one.
            var datarefAircraftName0 = new DataRefElement() { DataRef = "sim/aircraft/view/acf_ui_name[0]", Frequency = 1, Value = 0 };
            var datarefAircraftName2 = new DataRefElement() { DataRef = "sim/aircraft/view/acf_ui_name[2]", Frequency = 1, Value = 0 };

            Connector.Unsubscribe(datarefAircraftName0.DataRef);
            Connector.Unsubscribe(datarefAircraftName2.DataRef);
            Connector.Subscribe(datarefAircraftName0, 1, (e, v) =>
            {
                Log.Instance.log($"sim/aircraft/view/acf_ui_name[0] = {v}", LogSeverity.Debug);
                UpdateAircraftSubscription();
            });
            Connector.Subscribe(datarefAircraftName2, 1, (e, v) =>
            {
                Log.Instance.log($"sim/aircraft/view/acf_ui_name[2] = {v}", LogSeverity.Debug);
                UpdateAircraftSubscription();
            });
        }

        public bool Disconnect()
        {
            if (_connected)
            {
                _detectedAircraft = string.Empty;
                AircraftChanged?.Invoke(this, _detectedAircraft);
                _connected = false;
                ResetConnector();
                Closed?.Invoke(this, new EventArgs());
            }

            return _connected;
        }

        public bool IsConnected()
        {
            return _connected;
        }

        public void Start()
        {
            UnsubscribeAll();
        }

        public void Stop()
        {
            UnsubscribeAll();
        }

        private void UnsubscribeAll()
        {
            if (Connector != null)
            {
                foreach (var dataRef in SubscribedDataRefs)
                {
                    Connector.Unsubscribe(dataRef.Value.DataRef);
                }
            }

            SubscribedDataRefs.Clear();
        }

        public void Clear()
        {
            UnsubscribeAll();
        }

        public float readDataRef(string dataRefPath)
        {
            if (Connector == null) return 0;

            if (!SubscribedDataRefs.ContainsKey(dataRefPath))
            {
                var dataRefElement = new DataRefElement() { DataRef = dataRefPath, Frequency = UpdateFrequencyPerSecond, Value = 0 };
                SubscribedDataRefs.Add(dataRefPath, dataRefElement);
                Connector.Subscribe(dataRefElement, UpdateFrequencyPerSecond, (e, v) =>
                {
                    _lastDataReceived = DateTime.UtcNow;
                    SubscribedDataRefs[e.DataRef].Value = v;
                });
            }

            // make it extra safe when reading the value
            if (!SubscribedDataRefs.TryGetValue(dataRefPath, out var data)) return 0;

            return data.Value;
        }

        public void writeDataRef(string dataRefPath, float value)
        {
            Connector?.SetDataRefValue(dataRefPath, value);
        }

        public void sendCommand(string command)
        {
            XPlaneCommand xPlaneCommand = new XPlaneCommand(command, command);
            Connector?.SendCommand(xPlaneCommand);
        }
    }
}
