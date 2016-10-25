using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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
                VirtualMachineSnapshotTree snapshot = _underlyingVM.Snapshot.RootSnapshotList.Single(x => x.Name == snapshotNameOrID || x.Id.ToString() == snapshotNameOrID);

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
                    startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
                }
            });
        }

        private void doWithRetryOnSomeExceptions(Action thingtoDo, TimeSpan retry = default(TimeSpan), int maxRetries = 0)
        {
            int retries = maxRetries;
            if (retry == default(TimeSpan))
                retry = TimeSpan.Zero;

            while (true)
            {
                try
                {
                    thingtoDo.Invoke();
                    break;
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
                    _underlyingVM.PowerOffVM();

                _underlyingVM.PowerOnVM(_underlyingVM.Runtime.Host);
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
                if (_underlyingVM.Runtime.PowerState != VirtualMachinePowerState.poweredOn &&
                    _underlyingVM.Runtime.PowerState != VirtualMachinePowerState.suspended)
                    return;
            }
            // Sometimes I am seeing 'the attepnted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _underlyingVM.PowerOffVM();
                }
            }, TimeSpan.FromSeconds(1), 10  );
        }


        public override void copyToGuest(string srcpath, string dstpath)
        {
            doWithRetryOnSomeExceptions(() =>
            {
                lock (VMWareLock)
                {
                    _copyToGuest(srcpath, dstpath);
                }
            }, TimeSpan.FromMilliseconds(100), 100);
        }

        private void _copyToGuest(string srcpath, string dstpath)
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

        public override void startExecutable(string toExecute, string args)
        {
            _startExecutable(toExecute, args, true);
        }

        public void startExecutableAsync(string toExecute, string args)
        {
            _startExecutable(toExecute, args, false);
        }

        private void _startExecutable(string toExecute, string args, bool waitForExit)
        {
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