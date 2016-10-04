using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_iLo :  hypSpec_withWindbgKernel
    {
        public hypSpec_iLo(string hostIP, 
            string hostUsername, string hostPassword, 
            string iloHostname, string iLoUsername, string iloPassword,
            string iscsiServerIP, string iscsiServerUsername, string iscsiServerPassword, 
            string extentPrefix, UInt16 hostKernelDebugPort, string hostKernelDebugKey)
            : base(hostIP, hostKernelDebugPort, hostKernelDebugKey)
        {
            this.hostUsername = hostUsername;
            this.hostPassword = hostPassword;
            this.iLoHostname = iloHostname;
            this.iLoUsername = iLoUsername;
            this.iLoPassword = iloPassword;
            this.iscsiserverIP = iscsiServerIP;
            this.iscsiServerUsername = iscsiServerUsername;
            this.iscsiServerPassword = iscsiServerPassword;
            this.extentPrefix = extentPrefix;
        }
        public string hostUsername;
        public string hostPassword;
        public string iLoHostname;
        public string iLoUsername;
        public string iLoPassword;
        public string iscsiserverIP;
        public string iscsiServerUsername;
        public string iscsiServerPassword;
        public string extentPrefix;
    }
}