using System;

namespace hypervisors
{
    [Serializable]
    public class hypSpec_vmware : hypSpec_withWindbgKernel
    {
        public string kernelVMServer;
        public string kernelVMServerUsername;
        public string kernelVMServerPassword;
        public string kernelVMUsername;
        public string kernelVMPassword;
        public string kernelVMName;

        public hypSpec_vmware(
            string kernelVmName,
            string kernelVmServer, string kernelVmServerUsername, string kernelVmServerPassword,
            string kernelVmUsername, string kernelVmPassword,
            string snapshotFriendlyName, string snapshotNASPath,
            ushort kernelDebugPort, string kernelVMKey, string vmIPIOrHostname, 
            string kernelVmDebugSerialPort = null, string newKDProxyIPAddress = null, 
            kernelConnectionMethod newDebugMethod = kernelConnectionMethod.none)
            : base(vmIPIOrHostname, snapshotFriendlyName, snapshotNASPath, kernelDebugPort, kernelVMKey, kernelVmDebugSerialPort, newKDProxyIPAddress, newDebugMethod)
        {
            kernelVMName = kernelVmName;
            kernelVMServer = kernelVmServer;
            kernelVMServerUsername = kernelVmServerUsername;
            kernelVMServerPassword = kernelVmServerPassword;
            kernelVMUsername = kernelVmUsername;
            kernelVMPassword = kernelVmPassword;
        }
    }
}