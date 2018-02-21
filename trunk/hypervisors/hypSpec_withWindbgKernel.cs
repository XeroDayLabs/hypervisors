using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_withWindbgKernel
    {
        public hypSpec_withWindbgKernel(string IPOrHostname, string snapshotName, string newSnapshotFullName, ushort port, string key, string serialPortName = null)
        {
            kernelDebugIPOrHostname = IPOrHostname;
            kernelDebugPort = port;
            kernelDebugSerialPort = serialPortName;
            kernelDebugKey = key;
            snapshotFriendlyName = snapshotName;
            snapshotFullName = newSnapshotFullName;
        }

        /// <summary>
        /// The IP address or hostname of the blade/VM itself
        /// </summary>
        public string kernelDebugIPOrHostname;

        /// <summary>
        /// The UDP port to use when accepting KD sessions
        /// </summary>
        public ushort kernelDebugPort;

        /// <summary>
        /// The serial port to connect to, or null
        /// </summary>
        public string kernelDebugSerialPort;

        /// <summary>
        /// The key to use when accepting KD sessions
        /// </summary>
        public string kernelDebugKey;

        /// <summary>
        /// The friendly name for the snapshot, eg, 'clean'
        /// </summary>
        public string snapshotFriendlyName;

        /// <summary>
        /// The name of the snapshot on the NAS box, eg, "172.17.1.1-172.16.1.1-clean".
        /// Used only with FreeNAS-based snapshotting.
        /// </summary>
        public string snapshotFullName;
    }
}