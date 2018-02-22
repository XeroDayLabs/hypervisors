using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_iLo : hypSpec_withWindbgKernel
    {
        public hypSpec_iLo(string hostIP,
            string hostUsername, string hostPassword,
            string iloHostname, string iLoUsername, string iloPassword,
            string iscsiServerIP, string iscsiServerUsername, string iscsiServerPassword,
            string snapshotName, string snapshotPath, 
            UInt16 hostKernelDebugPort, string hostKernelDebugKey,
            string hostKernelDebugSerialPort = null, string KDProxyIPAddress = null,
            kernelConnectionMethod debugMethod = kernelConnectionMethod.none)
            : base(hostIP, snapshotName, snapshotPath, hostKernelDebugPort, hostKernelDebugKey, hostKernelDebugSerialPort, KDProxyIPAddress, debugMethod)
        {
            this.hostUsername = hostUsername;
            this.hostPassword = hostPassword;
            this.iLoHostname = iloHostname;
            this.iLoUsername = iLoUsername;
            this.iLoPassword = iloPassword;
            this.iscsiserverIP = iscsiServerIP;
            this.iscsiServerUsername = iscsiServerUsername;
            this.iscsiServerPassword = iscsiServerPassword;
            this.snapshotFullName = snapshotPath;
        }

        public string hostUsername;
        public string hostPassword;
        public string iLoHostname;
        public string iLoUsername;
        public string iLoPassword;
        public string iscsiserverIP;
        public string iscsiServerUsername;
        public string iscsiServerPassword;
    }
}