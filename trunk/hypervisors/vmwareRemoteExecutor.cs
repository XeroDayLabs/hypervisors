using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using VMware.Vim;

namespace hypervisors
{
    public class vmwareRemoteExecutor : IRemoteExecution
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

        public void mkdir(string newDir)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)_vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            GFM.MakeDirectoryInGuest(_underlyingVM.MoRef, Auth, newDir, true);
        }

        public void copyToGuest(string srcpath, string dstpath)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)_vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = _vClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = _vClient.GetView(gom.FileManager, null) as GuestFileManager;

            System.IO.FileInfo FileToTransfer = new System.IO.FileInfo(srcpath);
            GuestFileAttributes GFA = new GuestFileAttributes()
            {
                AccessTime = FileToTransfer.LastAccessTimeUtc,
                ModificationTime = FileToTransfer.LastWriteTimeUtc
            };

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

        public string getFileFromGuest(string srcpath)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)_vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
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

        public executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            // Execute via cmd.exe so we can capture stdout.
            string stdoutfilename = string.Format("C:\\windows\\temp\\hyp_stdout.txt");
            string stderrfilename = string.Format("C:\\windows\\temp\\hyp_stderr.txt");

            string cmdargs = String.Format("/c {0} {1} ", toExecute, args);
            cmdargs += " 1> " + stdoutfilename;
            cmdargs += " 2> " + stderrfilename;
            _startExecutable("cmd.exe", cmdargs, true, workingDir);

            return new executionResult()
            {
                stderr = getFileFromGuest(stderrfilename),
                stdout = getFileFromGuest(stdoutfilename)
            };
        }

        public void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {

            // Execute via cmd.exe so we can capture stdout.
            string cmdargs = String.Format("/c {0} {1} ", toExecute, args);
            if (stdoutfilename != null)
                cmdargs += "1> " + stdoutfilename;
            if (stderrfilename != null)
                cmdargs += "2> " + stderrfilename;
            if (retCodeFilename != null)
                cmdargs += " & echo %ERRORLEVEL% > " + retCodeFilename;

            _startExecutable("cmd.exe", cmdargs, false, workingDir);
        }

        public void testConnectivity()
        {
            executionResult res = startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();            
        }

        private void _startExecutable(string toExecute, string args, bool waitForExit, string workingDir = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            long guestPID;
            GuestProcessManager guestProcessManager;
            NamePasswordAuthentication Auth;
            //lock (VMWareLock)
            {
                Auth = new NamePasswordAuthentication
                {
                    Username = _spec.kernelVMUsername,
                    Password = _spec.kernelVMPassword,
                    InteractiveSession = true
                };
                GuestOperationsManager gom = (GuestOperationsManager)_vClient.GetView(_vClient.ServiceContent.GuestOperationsManager, null);
                GuestAuthManager guestAuthManager = (GuestAuthManager)_vClient.GetView(gom.AuthManager, null);
                guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
                guestProcessManager = _vClient.GetView(gom.ProcessManager, null) as GuestProcessManager;
                GuestProgramSpec progSpec = new GuestProgramSpec
                {
                    ProgramPath = toExecute,
                    Arguments = args
                };
                guestPID = guestProcessManager.StartProgramInGuest(_underlyingVM.MoRef, Auth, progSpec);

                if (!waitForExit)
                    return;
            }

            // Poll until specified pid exits. ( :/ )
            Stopwatch timeoutWatch = new Stopwatch();
            timeoutWatch.Start();
            long[] pids = new[] { guestPID };
            while (true)
            {
                try
                {
                    GuestProcessInfo[] info;
                    //lock (VMWareLock)
                    {
                        _underlyingVM.UpdateViewData();
                        info = guestProcessManager.ListProcessesInGuest(_underlyingVM.MoRef, Auth, pids);
                    }
                    if (info[0].EndTime != null)
                        break;
                }
                catch (VimException)
                {
                    Thread.Sleep(1000);
                }
                if (timeoutWatch.ElapsedMilliseconds > 60 * 1000)
                    break;
            }
        }
    
    }
}