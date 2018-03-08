using System;

namespace hypervisors
{
    public enum kernelConnectionMethod
    {
        none,
        serial,
        net,
    }

    [Serializable]
    public class hypSpec_withWindbgKernel
    {
        public hypSpec_withWindbgKernel(
            string IPOrHostname, string snapshotName, string newSnapshotFullName,
            ushort debugPort = 0, string debugKey = null,
            string serialPortName = null, string newKDProxyIPAddress = null, 
            kernelConnectionMethod newDebugMethod = kernelConnectionMethod.none)
        {
            kernelDebugIPOrHostname = IPOrHostname;
            kernelDebugPort = debugPort;
            kernelDebugSerialPort = serialPortName;
            kernelDebugKey = debugKey;
            KDProxyIPAddress = newKDProxyIPAddress;
            debugMethod = newDebugMethod;
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
        /// The key to use when accepting KD sessions, or null
        /// </summary>
        public string kernelDebugKey;

        /// <summary>
        /// The IP address of the machine to use as a KDProxy, or null if none is in use
        /// </summary>
        public string KDProxyIPAddress;

        /// <summary>
        /// How we should conenct to KD, if at all
        /// </summary>
        public kernelConnectionMethod debugMethod;

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