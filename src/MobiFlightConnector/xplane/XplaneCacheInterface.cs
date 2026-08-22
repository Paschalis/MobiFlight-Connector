namespace MobiFlight.xplane
{
    public interface XplaneCacheInterface : Base.CacheInterface
    {
        int UpdateFrequencyPerSecond { get; set; }

        /// <summary>
        /// The endpoint of the X-Plane instance to connect to.
        /// </summary>
        XplaneConnectionSettings Settings { get; set; }

        void Start();

        void Stop();

        /// <summary>
        /// Verifies that the sim is still sending data and raises ConnectionLost when it is not.
        /// </summary>
        void CheckConnectionStatus();

        float readDataRef(string dataRefPath);

        void writeDataRef(string dataRefPath, float value);

        void sendCommand(string command);
    }
}
