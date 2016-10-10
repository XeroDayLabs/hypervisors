using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public class resp
    {
        public string text;
    }

    [Serializable]
    public class powerShellException : Exception
    {
        private string text;

        public powerShellException()
        {
            // for XML de/ser
        }

        public powerShellException(PowerShell psContext)
        {
            StringBuilder errText = new StringBuilder();
            foreach (PSDataCollection<ErrorRecord> err in psContext.Streams.Error)
            {
                foreach (ErrorRecord errRecord in err)
                    errText.AppendLine( errRecord.ToString());
            }
            text = errText.ToString();
        }

        public override string Message
        {
            get { return text; }
        }
    }

    /// <summary>
    /// This class exposes a bare windows box. It uses SMB to copy files, spawn processes (by shelling out to psexec), and provides
    /// no snapshotting capability.
    /// It assumes the root of C:\ is shared as "C" on the guest.
    /// </summary>
    public class hypervisor_null : hypervisor
    {
        string _guestIP;
        string _username;
        string _password;
        private NetworkCredential _cred;

        public hypervisor_null(string guestIP, string _guestUsername, string _guestPassword)
        {
            _guestIP = guestIP;
            _username = _guestUsername;
            _password = _guestPassword;
            _cred = new NetworkCredential(_username, _password);
        }

        public override void connect()
        {
        }

        public override void powerOn()
        {
        }

        public override void powerOff()
        {
        }

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            throw new NotImplementedException();
        }

        public override void startExecutable(string toExecute, string cmdArgs)
        {
            string args = string.Format("\\\\{0} -u {1} -p {2} -h {3} {4}", _guestIP, _username, _password, toExecute, cmdArgs);
            ProcessStartInfo info = new ProcessStartInfo("psexec.exe", args);
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Normal;

            Process proc = Process.Start(info);

            proc.WaitForExit();

            string stderr = proc.StandardError.ReadToEnd();
            string stdout = proc.StandardOutput.ReadToEnd();
            if (proc.ExitCode != 0)
                throw new psExecException(stderr, proc.ExitCode);
            Debug.WriteLine("psexec on " + _guestIP + ": " + stderr + " / " + stdout);
        }

        public override void mkdir(string newDir)
        {
            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, newDir.Substring(2));
            int retries = 10;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        Directory.CreateDirectory(destUNC);
                    }
                    break;
                }
                catch (Win32Exception e)
                {
                    if (e.NativeErrorCode == 1219)  // "multiple connections to a server are not allowed"
                        throw;
                    if (retries-- == 0)
                        throw;
                }
                catch (IOException)
                {
                    if (retries-- == 0)
                        throw;
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {   
            if (!dstpath.ToLower().StartsWith("c:"))
                throw new Exception("Only C:\\ is shared");

            string destUNC = string.Format("\\\\{0}\\C{1}", _guestIP, dstpath.Substring(2));
            if (destUNC.EndsWith("\\"))
                destUNC += Path.GetFileName(srcpath);

            int retries = 10;
            while (true)
            {
                try
                {
                    using (NetworkConnection conn = new NetworkConnection(string.Format("\\\\{0}\\C", _guestIP), _cred))
                    {
                        System.IO.File.Copy(srcpath, destUNC);
                    }
                    break;
                }
                catch (Win32Exception)
                {
                    if (retries-- == 0)
                        throw;
                }
                catch (IOException)
                {
                    if (retries-- == 0)
                        throw;
                }
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Serializable()]
    public class psExecException : Exception
    {
        public readonly string stderr;
        public readonly int exitCode;

        public psExecException(string stderr, int exitCode) : base("PSExec error " + exitCode)
        {
            this.stderr = stderr;
            this.exitCode = exitCode;
        }

        // Needed for serialisation, apparently
        protected psExecException(SerializationInfo info, StreamingContext ctx)
            : base(info, ctx)
        {
            
        }

        public override string Message
        {
            get { return "code " + exitCode + "; stderr '" + stderr + "'"; }
        }
    }

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
        public string snapshotName;
    }

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
            ushort kernelDebugPort, string kernelVMKey, string vmIPIOrHostname)
            : base(vmIPIOrHostname, kernelDebugPort, kernelVMKey)
        {
            kernelVMName = kernelVmName;
            kernelVMServer = kernelVmServer;
            kernelVMServerUsername = kernelVmServerUsername;
            kernelVMServerPassword = kernelVmServerPassword;
            kernelVMUsername = kernelVmUsername;
            kernelVMPassword = kernelVmPassword;
        }

    }

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


    [StructLayout(LayoutKind.Sequential)]
    public class NetResource
    {
        public ResourceScope Scope;
        public ResourceType ResourceType;
        public ResourceDisplaytype DisplayType;
        public int Usage;
        public string LocalName;
        public string RemoteName;
        public string Comment;
        public string Provider;
    }

    public enum ResourceScope : int
    {
        Connected = 1,
        GlobalNetwork,
        Remembered,
        Recent,
        Context
    };

    public enum ResourceType : int
    {
        Any = 0,
        Disk = 1,
        Print = 2,
        Reserved = 8,
    }

    public enum ResourceDisplaytype : int
    {
        Generic = 0x0,
        Domain = 0x01,
        Server = 0x02,
        Share = 0x03,
        File = 0x04,
        Group = 0x05,
        Network = 0x06,
        Root = 0x07,
        Shareadmin = 0x08,
        Directory = 0x09,
        Tree = 0x0a,
        Ndscontainer = 0x0b
    }


}



