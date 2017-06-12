using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_withWindbgKernel
    {
        public hypSpec_withWindbgKernel(string IPOrHostname, string snapshotName, ushort port, string key)
        {
            kernelDebugIPOrHostname = IPOrHostname;
            kernelDebugPort = port;
            kernelDebugKey = key;
            snapshotFriendlyName = snapshotName;
        }

        /// <summary>
        /// The IP address or hostname of the blade/VM itself
        /// </summary>
        public string kernelDebugIPOrHostname;

        /// <summary>
        /// The port to use when accepting KD sessions
        /// </summary>
        public ushort kernelDebugPort;

        /// <summary>
        /// The key to use when accepting KD sessions
        /// </summary>
        public string kernelDebugKey;

        /// <summary>
        /// The friendly name for the snapshot, eg, 'clean'
        /// </summary>
        public string snapshotFriendlyName;

    }
}