using System;
using System.Runtime.Remoting.Lifetime;

namespace hypervisors
{
    /// <summary>
    /// This is a 'sponsor class', which is consulted by .net remoting when a "lease" is about to expire on an object.
    /// See 'MSDN magazine December 2003', "Remoting: Managing the lifetime of remote .NET Objects with leasing and sponsorship".
    /// </summary>
    public class hypervisor_iLo_sponsor : MarshalByRefObject, ISponsor 
    {
        public override object InitializeLifetimeService()
        {
            // The sponsor itself needs an infinite lease.
            return null;
        }

        public TimeSpan Renewal(ILease lease)
        {
            return TimeSpan.FromMinutes(10);
        }
    }
}