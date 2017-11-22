using System;
using System.Diagnostics;
using System.Net;

namespace hypervisors
{
    public class Icmp
    {
        private readonly string _hostnameOrIp;

        public Icmp(string hostnameOrIP)
        {
            _hostnameOrIp = hostnameOrIP;
        }

        public Icmp(IPAddress IP)
            : this(IP.ToString())
        {

        }

        /// <summary>
        /// It kinda sucks to shell out to ping.exe, but it's the best (?) safe way to do things.
        /// 
        /// We've had a lot of BSoDs caused by pinging via .net, with status DRIVER_LEFT_LOCKED_PAGES_IN_PROGRESS or
        /// PROCESS_HAS_LOCKED_PAGES. The same happens (according to the internet) when the windows API is used to ping. 
        /// Googling about shows that this has been the case for some time (since win7 - I write this in the win10 era)
        /// although there is some debate as to the root cause - a third party driver or windows itself. Either way, it
        /// sucks, so we do this.
        /// Previously, we would use some third party code which opened a raw packet and crafted an ICMP echo, then waited
        /// for an ICMP reply, which worked beautifully - but required administrator privs. Google advises that there's no
        /// way to give a user permission to use raw sockets (aside from becoming an Administrator), so we don't do that.
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public bool Ping(TimeSpan timeout)
        {
            ProcessStartInfo psi = new ProcessStartInfo("ping.exe", string.Format("-n 1 -w {0} {1}", timeout.TotalMilliseconds, _hostnameOrIp));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode == 0)
                    return true;
                else
                    return false;
            }

        }
    }
}