using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using hypervisors;

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
                default:
                    throw new ArgumentOutOfRangeException();
            }
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

        [Option('n', "numeric-output", Required = false, HelpText = "Output numeric responses, not ascii", DefaultValue = false)]
        public bool numeric { get; set; }
    }

    public enum hypervisorAction
    {
        powerOn,
        powerOff,
        getPowerStatus,
        getPowerUse
    }
}
