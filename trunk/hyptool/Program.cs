using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using hypervisors;
using Ysq.Zabbix;

namespace hyptool
{
    static class Program
    {
        static void Main(string[] args)
        {
            Parser parser = new CommandLine.Parser();
            hyptoolargs parsedArgs = new hyptoolargs();
            if (parser.ParseArgumentsStrict(args, parsedArgs, () => { Console.Write(HelpText.AutoBuild(parsedArgs).ToString()); }))
               _Main(parsedArgs);
        }

        private static void _Main(hyptoolargs args)
        {
            hypervisor_iLo_HTTP hyp = new hypervisor_iLo_HTTP(args.hypIP, args.hypUsername, args.hypPassword);
            hyp.retries = args.retries;
            hyp.connect();
            switch (args.action)
            {
                case hypervisorAction.powerOn:
                    hyp.powerOn();
                    break;
                case hypervisorAction.powerOff:
                    hyp.powerOff();
                    break;
                case hypervisorAction.getPowerStatus:
                    if (hyp.getPowerStatus())
                        Console.WriteLine(args.numeric ? "1" : "ON");
                    else
                        Console.WriteLine(args.numeric ? "0" : "OFF");
                    break;
                case hypervisorAction.getPowerUse:
                    Console.WriteLine(hyp.getCurrentPowerUseW());
                    break;
                case hypervisorAction.updateZabbix:
                    doZabbix(args.zabbixServer.Trim(), args.zabbixHostname.Trim(), hyp);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void doZabbix(string zbxServer, string ourHostname, hypervisor_iLo_HTTP hyp)
        {
            bool powerStatus = hyp.getPowerStatus();
            int powerUse = hyp.getCurrentPowerUseW();
            ilo_resp_healthfans fans = hyp.getHealthOfFans();
            ilo_resp_healthPSUs psus = hyp.getHealthOfPSUs();
            ilo_resp_healthtemps temps = hyp.getHealthOfTemps();

            Sender sender = new Ysq.Zabbix.Sender(zbxServer);
            // Send the discovery item a list of the data we have..
            sender.Send(ourHostname, "fanList", makeFanListJSON(fans), 2500);
            sender.Send(ourHostname, "psuList", makePSUListJSON(psus), 2500);
            sender.Send(ourHostname, "tempList", makeTempListJSON(temps), 2500);

            // and then send the data.
            sender.Send(ourHostname, "powerState", powerStatus ? "1" : "0");
            sender.Send(ourHostname, "powerUse", powerUse.ToString());
            foreach (ilo_resp_healthfan fan in fans.fans)
                sender.Send(ourHostname, "fanspeed[" + fan.label + "]", fan.speed);
            foreach (ilo_resp_healthPSU psu in psus.power_supplies)
                sender.Send(ourHostname, "psustatus[" + psu.label + "]", psu.status);
            foreach (ilo_resp_healthtemp temp in temps.temperature)
            {
                sender.Send(ourHostname, "cautionTemp[" + temp.label + "]", temp.caution.ToString());
                sender.Send(ourHostname, "currentTemp[" + temp.label + "]", temp.currentreading.ToString());
                sender.Send(ourHostname, "criticalTemp[" + temp.label + "]", temp.critical.ToString());
            }
        }

        private static string makeFanListJSON(ilo_resp_healthfans fans)
        {
            StringBuilder toSend = new StringBuilder("{ \"data\" :[ ");
            int n = 0;
            foreach (ilo_resp_healthfan fan in fans.fans)
            {
                if (n++ > 0)
                    toSend.Append(",");
                toSend.Append(String.Format("{{ \"{{#FANNAME}}\": \"{0}\"}}", fan.label));
            }
            toSend.Append("]}");

            return toSend.ToString();
        }

        private static string makePSUListJSON(ilo_resp_healthPSUs psus)
        {
            StringBuilder toSend = new StringBuilder("{ \"data\" :[ ");
            int n = 0;
            foreach (ilo_resp_healthPSU psu in psus.power_supplies)
            {
                if (n++ > 0)
                    toSend.Append(",");
                toSend.Append(String.Format("{{ \"{{#PSUNAME}}\": \"{0}\"}}", psu.label));
            }
            toSend.Append("]}");

            return toSend.ToString();
        }

        private static string makeTempListJSON(ilo_resp_healthtemps temps)
        {
            StringBuilder toSend = new StringBuilder("{ \"data\" :[ ");
            int n = 0;
            foreach (ilo_resp_healthtemp temp in temps.temperature)
            {
                if (n++ > 0)
                    toSend.Append(",");
                toSend.Append("{");
                toSend.Append(String.Format("\"{{#TEMPNAME}}\": \"{0}\",", temp.label));
                toSend.Append(String.Format("\"{{#TEMPLOCATION}}\": \"{0}\"", temp.location));
                toSend.Append("}");
            }
            toSend.Append("]}");

            return toSend.ToString();
        }
    }

    public class hyptoolargs
    {
        [Option('i', "IP", Required = true, HelpText = "IP address of hypervisor")]
        public string hypIP { get; set; }

        [Option('u', "username", Required = true, HelpText = "Username to present to hypervisor")]
        public string hypUsername { get; set; }

        [Option('p', "password", Required = true, HelpText = "Password to present to hypervisor")]
        public string hypPassword { get; set; }

        [Option('a', "action", Required = true, HelpText = "Action to perform")]
        public hypervisorAction action { get; set; }

        [Option('r', "retries", Required = false, HelpText = "Numer of retries on failure", DefaultValue = 10)]
        public int retries { get; set; }

        [Option('z', "zabbix-server", Required = false, HelpText = "Zabbix server to use", DefaultValue = "127.0.0.1")]
        public string zabbixServer { get; set; }

        [Option('h', "zabbix-hostname", Required = false, HelpText = "Hostname to report to Zabbix", DefaultValue = "localhost")]
        public string zabbixHostname { get; set; }

        [Option('n', "numeric-output", Required = false, HelpText = "Output numeric responses, not ascii", DefaultValue = false)]
        public bool numeric { get; set; }
    }

    public enum hypervisorAction
    {
        powerOn,
        powerOff,
        getPowerStatus,
        getPowerUse,
        updateZabbix
    }
}
