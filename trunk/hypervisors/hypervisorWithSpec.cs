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
                    bool pingOK = Icmp.Ping(Dns.GetHostAddresses(getBaseConnectionSpec().kernelDebugIPOrHostname).First());

                    if (pingOK)
                    {
                        Debug.Print(".. Box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " pingable, giving it a few more seconds..");
                        Thread.Sleep(TimeSpan.FromSeconds(10));
                        break;
                    }
                }
                else
                {
                    if (getPowerStatus() == false)
                        break;
                }

                Thread.Sleep(5000);
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