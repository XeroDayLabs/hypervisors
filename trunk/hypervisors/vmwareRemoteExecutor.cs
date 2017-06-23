using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using VMware.Vim;

namespace hypervisors
{
    public class vmwareRemoteExecutor : remoteExecution
    {
        private readonly hypSpec_vmware _spec;
        private readonly VimClientImpl _vClient;
        private readonly VirtualMachine _underlyingVM;

        public vmwareRemoteExecutor(hypSpec_vmware spec, VimClientImpl VClient, VirtualMachine underlyingVm)
        {
            _spec = spec;
            _vClient = VClient;
            _underlyingVM = underlyingVm;
        }

        public override void mkdir(string newDir)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            GFM.MakeDirectoryInGuest(_underlyingVM.MoRef, Auth, newDir, true);
        }

        public override void copyToGuest(string dstpath, string srcpath)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            System.IO.FileInfo FileToTransfer = new System.IO.FileInfo(srcpath);
            GuestFileAttributes GFA = new GuestFileAttributes()
            {
                AccessTime = FileToTransfer.LastAccessTimeUtc,
                ModificationTime = FileToTransfer.LastWriteTimeUtc
            };

            if (dstpath.EndsWith("\\"))
                dstpath += Path.GetFileName(srcpath);

            string transferOutput = GFM.InitiateFileTransferToGuest(_underlyingVM.MoRef, Auth, dstpath, GFA, FileToTransfer.Length, true);
            string nodeIpAddress = _vClient.ServiceUrl.ToString();
            nodeIpAddress = nodeIpAddress.Remove(nodeIpAddress.LastIndexOf('/'));
            transferOutput = transferOutput.Replace("https://*", nodeIpAddress);
            Uri oUri = new Uri(transferOutput);
            using (WebClient webClient = new WebClient())
            {
                webClient.UploadFile(oUri, "PUT", srcpath);
            }
        }

        public override string getFileFromGuest(string srcpath)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            FileTransferInformation transferOutput = GFM.InitiateFileTransferFromGuest(_underlyingVM.MoRef, Auth, srcpath);
            string nodeIpAddress = _vClient.ServiceUrl.ToString();
            nodeIpAddress = nodeIpAddress.Remove(nodeIpAddress.LastIndexOf('/'));
            string url = transferOutput.Url.Replace("https://*", nodeIpAddress);
            using (WebClient webClient = new WebClient())
            {
                return webClient.DownloadString(url);
            }
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            string tempDir = String.Format("C:\\users\\{0}\\", _spec.kernelVMUsername);

            if (workingDir == null)
                workingDir = tempDir;

            execFileSet fileSet = prepareForExecution(toExecute, args, tempDir);

            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };
            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = (GuestAuthManager) _vClient.GetView(gom.AuthManager, null);
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestProcessManager guestProcessManager = _vClient.GetView(gom.ProcessManager, null) as GuestProcessManager;
            GuestProgramSpec progSpec = new GuestProgramSpec
            {
                ProgramPath = fileSet.launcherPath,
                Arguments = "",
                WorkingDirectory = workingDir
            };
            guestProcessManager.StartProgramInGuest(_underlyingVM.MoRef, Auth, progSpec);

            return new asyncExecutionResultViaFile(this, fileSet);
        }

        public override void testConnectivity()
        {
            executionResult res = startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();
        }

        public override void deleteFile(string toDelete)
        {
            throw new NotImplementedException();
        }
    }
}