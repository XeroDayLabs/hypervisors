using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;

namespace hypervisors
{
    /// <summary>
    /// This is a hypervisor with a connection spec.
    /// </summary>
    /// <typeparam name="specType"></typeparam>
    public abstract class hypervisorWithSpec<specType> : hypervisor
    {
        public abstract specType getConnectionSpec();

        public abstract bool getPowerStatus();

        public hypSpec_withWindbgKernel getBaseConnectionSpec()
        {
            return getConnectionSpec() as hypSpec_withWindbgKernel;
        }

        private Action<specType> disposalCallback = null;

        public void setDisposalCallback(Action<specType> newDisposalCallback)
        {
            disposalCallback = newDisposalCallback;
        }

        public void waitForPingability(bool waitForState, cancellableDateTime deadline = null)
        {
            if (deadline == null)
                deadline = new cancellableDateTime();

            // Wait for the box to go down/come up.
            Debug.Print("Waiting for box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " to " + (waitForState ? "come up" : "go down"));
            while (true)
            {
                deadline.throwIfTimedOutOrCancelled();

                if (waitForState)
                {
                    Icmp pinger = new Icmp(Dns.GetHostAddresses(getBaseConnectionSpec().kernelDebugIPOrHostname).First());

                    if (pinger.Ping(TimeSpan.FromMilliseconds(500)))
                    {
                        Debug.Print(".. Box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " pingable, giving it a few more seconds..");
                        deadline.doCancellableSleep(TimeSpan.FromSeconds(10));
                        Debug.Print(".. Box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " assumed to be up now.");
                        break;
                    }
                }
                else
                {
                    if (getPowerStatus() == false)
                        break;
                }

                deadline.doCancellableSleep(TimeSpan.FromSeconds(5));
            }

            Debug.Print(".. wait complete for box " + getBaseConnectionSpec().kernelDebugIPOrHostname);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposalCallback != null)
                disposalCallback.Invoke(getConnectionSpec());

            base.Dispose(disposing);
        }
    }
}