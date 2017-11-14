using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Web.Services.Protocols;
using Org.Mentalis.Network;
using VMware.Vim;

namespace hypervisors
{
    public class cachedVIMClientConnection
    {
        private readonly hypSpec_vmware _spec;
        private VimClientImpl VClient = null;

        public cachedVIMClientConnection(hypSpec_vmware spec)
        {
            _spec = spec;

            //getMachine();
        }

        public VimClientImpl getConnection()
        {
            if (VClient == null)
                refreshVClient();

            // See if we can get a list of VMs. If this basic operation causes a timeout, then our session has probably expired
            // and we should reconnect.
            bool succeeded = false;
            List<EntityViewBase> vms;
            try
            {
                vms = VClient.FindEntityViews(typeof(VirtualMachine), null, null, null);
                if (vms != null)
                {
                    // FindEntityViews will return null if the session has timed out.
                    succeeded = true;
                }
            }
            catch (TimeoutException)
            {
                // ...
            }

            if (!succeeded)
            {
                Debug.WriteLine("Connection to ESXi server failed, reconnecting");
                refreshVClient();
            }

            // if _this_ one fails, that's fatal.
            vms = VClient.FindEntityViews(typeof(VirtualMachine), null, null, null);
            if (!succeeded)
                Debug.WriteLine("Connection to ESXi server reconnected OK.");

            return VClient;
        }

        private void refreshVClient()
        {
            // If we can't ping the box, assume we can't connect to the API either. We do this since I can't work out how to
            // set connection timeouts for the VMWare api (is there a way?).
            // We ping a few times, though, to allow for any packet loss going on.
            int pingRetries = 5;
            while (true)
            {
                Icmp pinger = new Icmp(_spec.kernelVMServer);
                TimeSpan res = pinger.Ping(TimeSpan.FromSeconds(3));
                if (res != TimeSpan.MaxValue)
                {
                    // Success, so continue.
                    break;
                }
                else
                {
                    // No response. If we have retries left, use one, or throw if not.
                    if (pingRetries == 0)
                        throw new WebException("Can't ping ESXi before trying to access web API");

                    pingRetries--;
                    Thread.Sleep(TimeSpan.FromSeconds(4));
                }
            }

            // We can ping fine, so connect using HTTP.
            DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(5);
            while (true)
            {
                try
                {
                    VClient = new VimClientImpl();
                    VClient.Connect("https://" + _spec.kernelVMServer + "/sdk");
                    VClient.Login(_spec.kernelVMServerUsername, _spec.kernelVMServerPassword);
                    break;
                }
                catch (Exception)
                {
                    if (DateTime.Now > deadline)
                        throw;
                    continue;
                }
            }
        }

        public VirtualMachine getMachine()
        {
            // Finally, we can get the VM.
            VimClientImpl impl = getConnection();
            List<EntityViewBase> vmlist = impl.FindEntityViews(typeof(VirtualMachine), null, null, null);
            VirtualMachine _underlyingVM = (VirtualMachine)vmlist.SingleOrDefault(x => ((VirtualMachine)x).Name.ToLower() == _spec.kernelVMName.ToLower());
            if (_underlyingVM == null)
                throw new VMNotFoundException("Can't find VM named '" + _spec.kernelVMName + "'");

            _underlyingVM.UpdateViewData();

            return _underlyingVM;
        }
    }

    public abstract class hypervisor_vmware_withoutSnapshots : hypervisorWithSpec<hypSpec_vmware>
    {
        /// <summary>
        /// Thje specification of this server - IP addresses, etc
        /// </summary>
        protected readonly hypSpec_vmware _spec;

        /// <summary>
        /// Our connection to the VMWare server
        /// </summary>
        protected cachedVIMClientConnection VClient;

        /// <summary>
        /// This can be used to select if executions will be performed via VMWare tools, or via psexec. Make sure that you take
        /// the neccessary steps for configuring SMB on the client machine, if you're going to use it.
        /// </summary>
        private clientExecutionMethod _executionMethod;

        /// <summary>
        /// The object that handles starting/stopping commands on the target, and also transferring files
        /// </summary>
        private remoteExecution executor;

        public static string[] getVMNames(string servername, string username, string password)
        {
            VimClientImpl client = new VimClientImpl();
            client.Connect("https://" + servername + "/sdk");
            client.Login(username, password);

            List<EntityViewBase> vmlist = client.FindEntityViews(typeof(VirtualMachine), null, null, null);

            return vmlist.Select(x => ((VirtualMachine) x).Name).ToArray();
        }

        protected hypervisor_vmware_withoutSnapshots(hypSpec_vmware spec, clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
        {
            _spec = spec;

            VClient = new cachedVIMClientConnection(_spec);

            _executionMethod = newExecMethod;
            switch (newExecMethod)
            {
                case clientExecutionMethod.vmwaretools:
                    executor = new vmwareRemoteExecutor(spec, VClient);
                    break;
                case clientExecutionMethod.smb:
                    executor = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.kernelVMUsername, spec.kernelVMPassword);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("newExecMethod", newExecMethod, null);
            }
        }

        public override void powerOn(cancellableDateTime deadline)
        {
            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            doWithRetryOnSomeExceptions(() =>
            {
                VirtualMachine VM = VClient.getMachine();
                if (VM.Runtime.PowerState == VirtualMachinePowerState.poweredOn)
                    return;
                VM.PowerOnVM(VM.Runtime.Host);
            }, deadline, TimeSpan.FromSeconds(5));

            // Wait for it to be ready
            if (_executionMethod == clientExecutionMethod.vmwaretools)
            {
                while (true)
                {
                    VirtualMachine VM = VClient.getMachine();
                    if (VM.Guest.ToolsRunningStatus == VirtualMachineToolsRunningStatus.guestToolsRunning.ToString())
                        break;

                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }

            // No, really, wait for it to be ready
            doWithRetryOnSomeExceptions(() =>
            {
                startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
                return "";
            }, deadline, TimeSpan.FromSeconds(5));
        }

        public override void powerOff(cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime();

            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            while (VClient.getMachine().Runtime.PowerState != VirtualMachinePowerState.poweredOff)
            {
                doWithRetryOnSomeExceptions(() =>
                {
                    VirtualMachine vm = VClient.getMachine();
                    vm.UpdateViewData();
                    if (vm.Runtime.PowerState == VirtualMachinePowerState.poweredOff)
                        return;
                    vm.PowerOffVM();
                }, deadline, TimeSpan.FromSeconds(5));
            }
        }

        public override void connect()
        {
        }

        public override void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(2));

            doWithRetryOnSomeExceptions(() => { _copyToGuest(dstpath, srcpath); }, deadline, TimeSpan.FromSeconds(10));
        }

        private void _copyToGuest(string dstpath, string srcpath)
        {
            executor.copyToGuest(dstpath, srcpath);
        }

        public override string getFileFromGuest(string srcpath, cancellableDateTime deadline)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(2));

            return doWithRetryOnSomeExceptions(() => { return executor.tryGetFileFromGuestWithRes(srcpath); }, deadline, TimeSpan.FromSeconds(10));
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime();

            return executor.startExecutable(toExecute, args, workingdir, deadline);
        }

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            return executor.startExecutableAsync(toExecute, args, workingDir);
        }

        public override IAsyncExecutionResult startExecutableAsyncWithRetry(string toExecute, string args, string workingDir = null)
        {
            return executor.startExecutableAsyncWithRetry(toExecute, args, workingDir);
        }

        public override void mkdir(string newDir, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime(TimeSpan.FromMinutes(1));

            doWithRetryOnSomeExceptions(() => { executor.mkdir(newDir); }, TimeSpan.FromSeconds(10));
        }

        public override hypSpec_vmware getConnectionSpec()
        {
            return _spec;
        }

        public override bool getPowerStatus()
        {
            var vm = VClient.getMachine();
            vm.UpdateViewData();
            if (vm.Runtime.PowerState == VirtualMachinePowerState.poweredOn)
                return true;
            else
                return false;
        }

        public override string ToString()
        {
            return string.Format("{0}:{1}", _spec.kernelDebugIPOrHostname, _spec.kernelDebugPort);
        }
    }

    public class VMNotFoundException : Exception
    {
        public VMNotFoundException(string msg)
            : base(msg)
        {
        }
    }

    /// <summary>
    /// A snapshottable VM, powered by FreeNAS / iscsi / PXE.
    /// </summary>
    public class hypervisor_vmware_FreeNAS : hypervisor_vmware_withoutSnapshots
    {
        private string _freeNASIP;
        private string _freeNASUsername;
        private string _freeNASPassword;

        public hypervisor_vmware_FreeNAS(hypSpec_vmware spec,
            NASParams nasParams,
            clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
            : base(spec, newExecMethod)
        {
            _freeNASIP = nasParams.IP;
            _freeNASUsername = nasParams.username;
            _freeNASPassword = nasParams.password;
        }

        public hypervisor_vmware_FreeNAS(hypSpec_vmware spec,
            string freeNasip, string freeNasUsername, string freeNasPassword, clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
            : base(spec, newExecMethod)
        {
            _freeNASIP = freeNasip;
            _freeNASUsername = freeNasUsername;
            _freeNASPassword = freeNasPassword;
        }

        public override void restoreSnapshot()
        {
            freeNASSnapshot.restoreSnapshot(this, _freeNASIP, _freeNASUsername, _freeNASPassword);
        }

        public override void WaitForStatus(bool isPowerOn, cancellableDateTime deadline)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// A snapshottable VMWare VM, using VMWare's built-in snapshot ability.
    /// </summary>
    public class hypervisor_vmware : hypervisor_vmware_withoutSnapshots
    {
        public hypervisor_vmware(hypSpec_vmware spec, clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
            : base(spec, newExecMethod)
        {
        }

        public override void restoreSnapshot()
        {
            VimClientImpl _vClient = VClient.getConnection();
            VirtualMachine vm = VClient.getMachine();

            // Find a named snapshot which corresponds to what we're interested in
            VirtualMachineSnapshotTree snapshot = findRecusively(vm.Snapshot.RootSnapshotList, _spec.snapshotFriendlyName);

            // and revert it.
            VirtualMachineSnapshot shot = new VirtualMachineSnapshot(_vClient, snapshot.Snapshot);
            shot.RevertToSnapshot(vm.MoRef, false);
        }

        public override void WaitForStatus(bool isPowerOn, cancellableDateTime deadline)
        {
            throw new NotImplementedException();
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
    }

    public enum clientExecutionMethod
    {
        vmwaretools,
        smb,
        SSHToBASH
    }
}