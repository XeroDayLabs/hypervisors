using System;
using System.Diagnostics;
using System.Net;

namespace Org.Mentalis.Network
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

        public bool Ping(TimeSpan timeout)
        {
            ProcessStartInfo psi = new ProcessStartInfo("ping.exe", "-n 1 -w timeout " + _hostnameOrIp);

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