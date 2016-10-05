using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_withWindbgKernel
    {
        public hypSpec_withWindbgKernel(string IPOrHostname, ushort port, string key)
        {
            kernelDebugIPOrHostname = IPOrHostname;
            kernelDebugPort = port;
            kernelDebugKey = key;
        }

        public string kernelDebugIPOrHostname;
        public ushort kernelDebugPort;
        public string kernelDebugKey;
    }
}