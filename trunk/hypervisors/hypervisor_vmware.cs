using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public class hypervisor_vmware : hypervisorWithSpec<hypSpec_vmware>
    {
        private readonly hypSpec_vmware _spec;

        // Maybe this will make all my weird vmware problems go away :^)
        private static Object VMWareLock = new Object();

        private VimClientImpl VClient;
        private VirtualMachine _underlyingVM;

        public hypervisor_vmware(hypSpec_vmware spec)
        {
            _spec = spec;
            lock (VMWareLock)
            {
                VClient = new VimClientImpl();
                VClient.Connect("https://" + spec.kernelVMServer + "/sdk");
                VClient.Login(spec.kernelVMServerUsername, spec.kernelVMServerPassword);

                List<EntityViewBase> vmlist = VClient.FindEntityViews(typeof (VirtualMachine), null, null, null);
                _underlyingVM = (VirtualMachine) vmlist.SingleOrDefault(x => ((VirtualMachine) x).Config.Name.ToLower() == spec.kernelVMName.ToLower());
                if (_underlyingVM == null)
                    throw new Exception("Can't find VM named '" + spec.kernelVMName + "'");
            }
        }

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            lock (VMWareLock)
            {
                // Find its named snapshot
                VirtualMachineSnapshotTree snapshot = findRecusively(_underlyingVM.Snapshot.RootSnapshotList, snapshotNameOrID);

                // and revert it.
                VirtualMachineSnapshot shot = new VirtualMachineSnapshot(VClient, snapshot.Snapshot);
                shot.RevertToSnapshot(_underlyingVM.MoRef, false);
            }
            // Wait for it to be ready
//            while (_underlyingVM.Guest.ToolsRunningStatus != VirtualMachineToolsRunningStatus.guestToolsRunning.ToString())
//                Thread.Sleep(100);

            // No, really, wait for it to be ready
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi", true);
                }
                return "";
            });
        }

        private VirtualMachineSnapshotTree findRecusively(VirtualMachineSnapshotTree[] parent, string snapshotNameOrID)
        {
            foreach (VirtualMachineSnapshotTree tree in parent)
            {
                if (tree.Name == snapshotNameOrID || tree.Id.ToString() == snapshotNameOrID)
                    return tree;

                return findRecusively(tree.ChildSnapshotList, snapshotNameOrID);
            }
            return null;
        }

        private string doWithRetryOnSomeExceptions(Func<string> thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
        {
            int retries = maxRetries;
            if (retry == default(TimeSpan))
                retry = TimeSpan.Zero;

            while (true)
            {
                try
                {
                    return thingtoDo.Invoke();
                }
                catch (VimException)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                catch (System.Net.WebException)
                {
                    if (maxRetries != 0)
                    {
                        if (retries-- == 0)
                            throw;
                    }
                }
                Thread.Sleep(retry);
            }
        }

        public override void connect()
        {
        }

        public override void powerOn()
        {
            lock (VMWareLock)
            {
                if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOn ||
                    _underlyingVM.Runtime.PowerState == VirtualMachinePowerState.suspended)
                    powerOff();

                // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
                // particularly under load, hence the retries.
                doWithRetryOnSomeExceptions(() =>
                {
                    lock (VMWareLock)
                    {
                        _underlyingVM.PowerOnVM(_underlyingVM.Runtime.Host);
                    }
                    return null;
                }, TimeSpan.FromSeconds(1), 10);

            }

            while (true)
            {
                lock (VMWareLock)
                {
                    if (_underlyingVM.Guest.ToolsRunningStatus == VirtualMachineToolsRunningStatus.guestToolsRunning.ToString())
                        break;
                }

                Thread.Sleep(100);
            }
        }

        public override void powerOff()
        {
            lock (VMWareLock)
            {
                if (_underlyingVM.Runtime.PowerState != VirtualMachinePowerState.poweredOff)
                    powerOn();
            }
            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _underlyingVM.PowerOffVM();
                }
                return null;
            }, TimeSpan.FromSeconds(1), 10  );
        }
        
        public override void copyToGuest(string srcpath, string dstpath, bool ignoreExising)
        {
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _copyToGuest(srcpath, dstpath, ignoreExising);
                }
                return null;
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        private void _copyToGuest(string srcpath, string dstpath, bool ignoreExisting)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)VClient.GetView(VClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = VClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = VClient.GetView(gom.FileManager, null) as GuestFileManager;

            System.IO.FileInfo FileToTransfer = new System.IO.FileInfo(srcpath);
            GuestFileAttributes GFA = new GuestFileAttributes()
            {
                AccessTime = FileToTransfer.LastAccessTimeUtc,
                ModificationTime = FileToTransfer.LastWriteTimeUtc
            };

            dstpath += Path.GetFileName(srcpath);
            
            string transferOutput = GFM.InitiateFileTransferToGuest(_underlyingVM.MoRef, Auth, dstpath, GFA, FileToTransfer.Length, true);
            string nodeIpAddress = VClient.ServiceUrl.ToString();
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
            return doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    return _getFileFromGuest(srcpath);
                }
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        private string _getFileFromGuest(string srcpath)
        {
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)VClient.GetView(VClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = VClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = VClient.GetView(gom.FileManager, null) as GuestFileManager;

            FileTransferInformation transferOutput = GFM.InitiateFileTransferFromGuest(_underlyingVM.MoRef, Auth, srcpath);
            string nodeIpAddress = VClient.ServiceUrl.ToString();
            nodeIpAddress = nodeIpAddress.Remove(nodeIpAddress.LastIndexOf('/'));
            string url = transferOutput.Url.Replace("https://*", nodeIpAddress);
            using (WebClient webClient = new WebClient())
            {
                return webClient.DownloadString(url);
            }
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null)
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

        public override void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
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

        private void _startExecutable(string toExecute, string args, bool waitForExit, string workingDir = null)
        {
            if (workingDir == null)
                workingDir = "C:\\";

            long guestPID;
            GuestProcessManager guestProcessManager;
            NamePasswordAuthentication Auth;
            lock (VMWareLock)
            {
                Auth = new NamePasswordAuthentication
                {
                    Username = _spec.kernelVMUsername,
                    Password = _spec.kernelVMPassword,
                    InteractiveSession = true
                };
                GuestOperationsManager gom = (GuestOperationsManager) VClient.GetView(VClient.ServiceContent.GuestOperationsManager, null);
                GuestAuthManager guestAuthManager = (GuestAuthManager) VClient.GetView(gom.AuthManager, null);
                guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
                guestProcessManager = VClient.GetView(gom.ProcessManager, null) as GuestProcessManager;
                GuestProgramSpec progSpec = new GuestProgramSpec
                {
                    ProgramPath = toExecute, Arguments = args
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
                    lock (VMWareLock)
                    {
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

        public override void mkdir(string newDir)
        {
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _mkdir(newDir);
                }
                return null;
            }, TimeSpan.FromMilliseconds(100), 100);
        }
        
        private void _mkdir(string newDir)
        {
            
            NamePasswordAuthentication Auth = new NamePasswordAuthentication
            {
                Username = _spec.kernelVMUsername,
                Password = _spec.kernelVMPassword,
                InteractiveSession = true
            };

            GuestOperationsManager gom = (GuestOperationsManager)VClient.GetView(VClient.ServiceContent.GuestOperationsManager, null);
            GuestAuthManager guestAuthManager = VClient.GetView(gom.AuthManager, null) as GuestAuthManager;
            guestAuthManager.ValidateCredentialsInGuest(_underlyingVM.MoRef, Auth);
            GuestFileManager GFM = VClient.GetView(gom.FileManager, null) as GuestFileManager;

            GFM.MakeDirectoryInGuest(_underlyingVM.MoRef, Auth, newDir, true);            
        }

        public override hypSpec_vmware getConnectionSpec()
        {
            return _spec;
        }

    }
}