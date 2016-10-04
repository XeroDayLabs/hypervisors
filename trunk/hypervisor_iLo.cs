using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Remoting.Lifetime;

namespace hypervisors
{
    /// <summary>
    /// Okay, so, the HP iLo powershell components aren't 'reusable' (as in, if you attempt to import them, they will fail
    /// while trying to extract themselves to a temporary file, since that file is created by another thread (!?)). To get
    /// around this, we run each in its own appDomain. Ick.
    /// </summary>
    public class hypervisor_iLo : hypervisorWithSpec<hypSpec_iLo>
    {
        private readonly AppDomain app;
        private readonly hypervisor_iLo_appdomainPayload wrapped;
        private readonly hypervisor_iLo_sponsor sponsor;

        public hypervisor_iLo(hypSpec_iLo spec)
        {
            // Create the new appdomain
            AppDomainSetup ads = new AppDomainSetup();
            string appBase = new Uri(Assembly.GetCallingAssembly().CodeBase).LocalPath;
            ads.ApplicationBase = Path.GetDirectoryName(appBase);
            app = AppDomain.CreateDomain("iLo comms appdomain for " + spec.iLoHostname, null, ads);
            try
            {
                // Prepare args for the constructor
                object[] cstrArgs = new object[] { spec };

                // Call the constructor and unwrap the result into a proxy object
                wrapped = (hypervisor_iLo_appdomainPayload)app.CreateInstanceFromAndUnwrap(
                    typeof(hypervisor_iLo_appdomainPayload).Assembly.CodeBase,
                    typeof(hypervisor_iLo_appdomainPayload).FullName,
                    false, BindingFlags.CreateInstance | BindingFlags.Public | BindingFlags.Instance,
                    null, cstrArgs, CultureInfo.CurrentCulture, null);

                sponsor = new hypervisor_iLo_sponsor();
                ILease lease = (ILease)wrapped.GetLifetimeService();
                lease.Register(sponsor);
            }
            catch (Exception)
            {
                // Ensure that we unload our appdomain if something goes wrong. If we don't, and there is an exception thrown,
                // then MSTest will wait forever.
                AppDomain.Unload(app);
                throw;
            }

        }

        public hypervisor_iLo(
            string hostIp, string iloHostUsername, string iloHostPassword, 
            string iLoIp, string iloUsername, string iloPassword, 
            string iloIscsiip, string iloIscsiUsername, string iloIscsiPassword, 
            string extentPrefix, 
            ushort kernelDebugPort, string kernelDebugKey)
            : this ( new hypSpec_iLo(
                hostIp, iloHostUsername, iloHostPassword,
                iLoIp, iloUsername, iloPassword, 
                iloIscsiip, iloIscsiUsername, iloIscsiPassword, 
                extentPrefix, 
                kernelDebugPort, kernelDebugKey))
        {
        }

        protected override void _Dispose()
        {
            ILease lease = (ILease)wrapped.GetLifetimeService();
            lease.Unregister(sponsor);

            wrapped.Dispose();

            base._Dispose();
        }

        public override void restoreSnapshotByName(string snapshotNameOrID)
        {
            wrapped.restoreSnapshotByName(snapshotNameOrID);
        }

        public override void connect()
        {
            wrapped.connect();
        }

        public override void powerOn()
        {
            wrapped.powerOn();
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {
            wrapped.copyToGuest(srcpath, dstpath);
        }

        public override void startExecutable(string toExecute, string args)
        {
            wrapped.startExecutable(toExecute, args);
        }

        public override void mkdir(string newDir)
        {
            wrapped.mkdir(newDir);
        }

        public override hypSpec_iLo getConnectionSpec()
        {
            return wrapped.getConnectionSpec();
        }

        public override void powerOff()
        {
            wrapped.powerOff();
        }

        public override string ToString()
        {
            return wrapped.ToString();
        }
    }
}