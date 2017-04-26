using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using Org.Mentalis.Network;

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

        public void WaitForStatus(bool waitForState, TimeSpan timeout = default(TimeSpan))
        {
            DateTime deadline;
            if (timeout == default(TimeSpan))
                deadline = DateTime.MaxValue;
            else
                deadline = DateTime.Now + timeout;

            // Wait for the box to go down/come up.
            Debug.Print("Waiting for box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " to " + (waitForState ? "come up" : "go down"));
            while (true)
            {
                if (DateTime.Now > deadline)
                    throw new TimeoutException();

                if (waitForState)
                {
                    Icmp pinger = new Org.Mentalis.Network.Icmp(IPAddress.Parse(getBaseConnectionSpec().kernelDebugIPOrHostname));
                    TimeSpan res = pinger.Ping(TimeSpan.FromMilliseconds(500));
                    if (res != TimeSpan.MaxValue)
                    {
                        Debug.Print(".. Box " + getBaseConnectionSpec().kernelDebugIPOrHostname + " pingable, giving it a few more seconds..");
                        Thread.Sleep(10 * 1000);
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
        
        protected override void _Dispose()
        {
            if (disposalCallback != null)
                disposalCallback.Invoke(getConnectionSpec());

            base._Dispose();
        }
    }
}