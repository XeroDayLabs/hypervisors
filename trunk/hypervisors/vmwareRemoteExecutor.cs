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
        private readonly cachedVIMClientConnection conn;

        public vmwareRemoteExecutor(hypSpec_vmware spec, cachedVIMClientConnection VClient)
        {
            _spec = spec;
            conn = VClient;
        }

        public override void mkdir(string newDir, cancellableDateTime deadline)
        {
            // TODO: timeouts
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            VimClientImpl _vClient = conn.getConnection();
            VirtualMachine _underlyingVM = conn.getMachine();

            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            GFM.MakeDirectoryInGuest(_underlyingVM.MoRef, Auth, newDir, true);
        }

        public override void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline = null)
        {
            if (!File.Exists(srcpath))
                throw new Exception("src file not found");

            // TODO: deadline is ignored here

            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            VimClientImpl _vClient = conn.getConnection();
            VirtualMachine _underlyingVM = conn.getMachine();

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

        public override string tryGetFileFromGuest(string srcpath, out Exception errorOrNull)
        {
            try
            {
                NamePasswordAuthentication Auth = new NamePasswordAuthentication
                {
                    Username = _spec.kernelVMUsername,
                    Password = _spec.kernelVMPassword,
                    InteractiveSession = true
                };

                VimClientImpl vClient = conn.getConnection();
                VirtualMachine underlyingVM = conn.getMachine();

                GuestOperationsManager gom = (GuestOperationsManager) vClient.GetView(vClient.ServiceContent.GuestOperationsManager, null);
                GuestAuthManager guestAuthManager = vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
                guestAuthManager.ValidateCredentialsInGuest(underlyingVM.MoRef, Auth);
                GuestFileManager GFM = vClient.GetView(gom.FileManager, null) as GuestFileManager;

                FileTransferInformation transferOutput = GFM.InitiateFileTransferFromGuest(underlyingVM.MoRef, Auth, srcpath);
                string nodeIpAddress = vClient.ServiceUrl;
                nodeIpAddress = nodeIpAddress.Remove(nodeIpAddress.LastIndexOf('/'));
                string url = transferOutput.Url.Replace("https://*", nodeIpAddress);
                using (WebClient webClient = new WebClient())
                {
                    errorOrNull = null;
                    return webClient.DownloadString(url);
                }
            }
            catch (Exception e)
            {
                errorOrNull = e;
                return null;
            }
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            string tempDir = String.Format("C:\\users\\{0}\\", _spec.kernelVMUsername);

            if (workingDir == null)
                workingDir = tempDir;

            execFileSet fileSet = prepareForExecution(toExecute, args, tempDir);

            NamePasswordAuthentication auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            VimClientImpl _vClient = conn.getConnection();
            VirtualMachine _underlyingVM = conn.getMachine();

            GuestOperationsManager gom = (GuestOperationsManager) _vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = (GuestAuthManager) _vClient.GetView(gom.AuthManager, null);
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, auth);
            GuestProcessManager guestProcessManager = _vClient.GetView(gom.ProcessManager, null) as GuestProcessManager;
            GuestProgramSpec progSpec = new GuestProgramSpec
            {
                ProgramPath = fileSet.launcherPath,
                Arguments = "",
                WorkingDirectory = workingDir
            };
            guestProcessManager.StartProgramInGuest(_underlyingVM.MoRef, auth, progSpec);

            return new asyncExecutionResultViaFile(this, fileSet);
        }

        public override void testConnectivity()
        {
            executionResult res = startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();
        }

        public override void deleteFile(string toDelete, cancellableDateTime deadline)
        {
            throw new NotImplementedException();
        }
    }
}