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
using Action = System.Action;

namespace hypervisors
{
    public abstract class hypervisor_vmware_withoutSnapshots : hypervisorWithSpec<hypSpec_vmware>
    {
        /// <summary>
        /// Thje specification of this server - IP addresses, etc
        /// </summary>
        protected readonly hypSpec_vmware _spec;

        /// <summary>
        /// Our connection to the VMWare server
        /// </summary>
        protected VimClientImpl VClient;

        /// <summary>
        /// The VMWare-exposed VM object
        /// </summary>
        protected VirtualMachine _underlyingVM;

        /// <summary>
        /// This can be used to select if executions will be performed via VMWare tools, or via psexec. Make sure that you take
        /// the neccessary steps for configuring SMB on the client machine, if you're going to use it.
        /// </summary>
        private clientExecutionMethod _executionMethod;

        /// <summary>
        /// The object that handles starting/stopping commands on the target, and also transferring files
        /// </summary>
        private remoteExecution executor;

        protected hypervisor_vmware_withoutSnapshots(hypSpec_vmware spec, clientExecutionMethod newExecMethod = clientExecutionMethod.vmwaretools)
        {
            _spec = spec;

            // If we can't ping the box, assume we can't connect to the API either. We do this since I can't work out how to
            // set connection timeouts for the VMWare api (is there a way?).
            // We ping a few times, though, to allow for any packet loss going on.
            int pingRetries = 5;
            while (true)
            {
                Icmp pinger = new Icmp(Dns.GetHostAddresses(spec.kernelVMServer).First());
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

            VClient = new VimClientImpl();
            VClient.Connect("https://" + _spec.kernelVMServer + "/sdk");
            VClient.Login(_spec.kernelVMServerUsername, _spec.kernelVMServerPassword);

            List<EntityViewBase> vmlist = VClient.FindEntityViews(typeof (VirtualMachine), null, null, null);
            _underlyingVM = (VirtualMachine) vmlist.SingleOrDefault(x => ((VirtualMachine) x).Name.ToLower() == _spec.kernelVMName.ToLower());
            if (_underlyingVM == null)
                throw new VMNotFoundException("Can't find VM named '" + _spec.kernelVMName + "'");

            _executionMethod = newExecMethod;
            switch (newExecMethod)
            {
                case clientExecutionMethod.vmwaretools:
                    executor = new vmwareRemoteExecutor(spec, VClient, _underlyingVM);
                    break;
                case clientExecutionMethod.smb:
                    executor = new SMBExecutor(spec.kernelDebugIPOrHostname, spec.kernelVMUsername, spec.kernelVMPassword);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("newExecMethod", newExecMethod, null);
            }
        }

        public override void powerOn(DateTime deadline = default(DateTime))
        {
            if (deadline == default(DateTime))
                deadline = DateTime.Now + TimeSpan.FromMinutes(3);

            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            doWithRetryOnSomeExceptions(() =>
            {
                //lock (VMWareLock)
                {
                    _underlyingVM.UpdateViewData();
                    if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOn)
                        return;
                    _underlyingVM.PowerOnVM(_underlyingVM.Runtime.Host);
                }
            }, TimeSpan.FromSeconds(5), deadline - DateTime.Now);

            // Wait for it to be ready
            if (_executionMethod == clientExecutionMethod.vmwaretools)
            {
                while (true)
                {
                    _underlyingVM.UpdateViewData();
                    if (_underlyingVM.Guest.ToolsRunningStatus == VirtualMachineToolsRunningStatus.guestToolsRunning.ToString())
                        break;

                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }

            // No, really, wait for it to be ready
            doWithRetryOnSomeExceptions(() =>
            {
                startExecutable("C:\\windows\\system32\\cmd.exe", "/c echo hi");
                return "";
            }, TimeSpan.FromSeconds(5), deadline - DateTime.Now);
        }

        public override void powerOff(DateTime deadline = default(DateTime))
        {
            if (deadline == default(DateTime))
                deadline = DateTime.Now + TimeSpan.FromMinutes(3);

            // Sometimes I am seeing 'the attempted operation cannot be performed in the current state (Powered on)' here,
            // particularly under load, hence the retries.
            _underlyingVM.UpdateViewData();
            while (_underlyingVM.Runtime.PowerState != VirtualMachinePowerState.poweredOff)
            {
                doWithRetryOnSomeExceptions(() =>
                {
                    _underlyingVM.UpdateViewData();
                    if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOff)
                        return;
                    _underlyingVM.PowerOffVM();
                }, TimeSpan.FromSeconds(5), deadline - DateTime.Now);
            }
        }

        public override void connect()
        {
        }

        public override void copyToGuest(string dstpath, string srcpath)
        {
            doWithRetryOnSomeExceptions(() => { _copyToGuest(dstpath, srcpath); }, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        private void _copyToGuest(string dstpath, string srcpath)
        {
            executor.copyToGuest(dstpath, srcpath);
        }

        public override string getFileFromGuest(string srcpath, TimeSpan timeout)
        {
            if (timeout == default(TimeSpan))
                timeout = TimeSpan.FromSeconds(30);

            return doWithRetryOnSomeExceptions(() => { return executor.getFileFromGuest(srcpath); }, TimeSpan.FromSeconds(10), timeout);
        }

        public override executionResult startExecutable(string toExecute, string args, string workingdir = null, DateTime deadline = default(DateTime))
        {
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

        public override void mkdir(string newDir)
        {
            doWithRetryOnSomeExceptions(() => { executor.mkdir(newDir); }, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
        }

        public override hypSpec_vmware getConnectionSpec()
        {
            return _spec;
        }

        public override bool getPowerStatus()
        {
            _underlyingVM.UpdateViewData();
            if (_underlyingVM.Runtime.PowerState == VirtualMachinePowerState.poweredOn)
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
            _underlyingVM.UpdateViewData();

            // Find a named snapshot which corresponds to what we're interested in
            VirtualMachineSnapshotTree snapshot = findRecusively(_underlyingVM.Snapshot.RootSnapshotList, _spec.snapshotFriendlyName);

            // and revert it.
            VirtualMachineSnapshot shot = new VirtualMachineSnapshot(VClient, snapshot.Snapshot);
            shot.RevertToSnapshot(_underlyingVM.MoRef, false);
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