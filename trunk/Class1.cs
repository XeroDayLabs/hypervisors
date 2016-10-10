using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VMware.Vim;
using Action = System.Action;

namespace hypervisors
{
    public class resp
    {
        public string text;
    }

    [Serializable]
    public class powerShellException : Exception
    {
        private string text;

        public powerShellException()
        {
            // for XML de/ser
        }

        public powerShellException(PowerShell psContext)
        {
            StringBuilder errText = new StringBuilder();
            foreach (PSDataCollection<ErrorRecord> err in psContext.Streams.Error)
            {
                foreach (ErrorRecord errRecord in err)
                    errText.AppendLine( errRecord.ToString());
            }
            text = errText.ToString();
        }

        public override string Message
        {
            get { return text; }
        }
    }

    [Serializable()]
    public class psExecException : Exception
    {
        public readonly string stderr;
        public readonly int exitCode;

        public psExecException(string stderr, int exitCode) : base("PSExec error " + exitCode)
        {
            this.stderr = stderr;
            this.exitCode = exitCode;
        }

        // Needed for serialisation, apparently
        protected psExecException(SerializationInfo info, StreamingContext ctx)
            : base(info, ctx)
        {
            
        }

        public override string Message
        {
            get { return "code " + exitCode + "; stderr '" + stderr + "'"; }
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    public class NetResource
    {
        public ResourceScope Scope;
        public ResourceType ResourceType;
        public ResourceDisplaytype DisplayType;
        public int Usage;
        public string LocalName;
        public string RemoteName;
        public string Comment;
        public string Provider;
    }

    public enum ResourceScope : int
    {
        Connected = 1,
        GlobalNetwork,
        Remembered,
        Recent,
        Context
    };

    public enum ResourceType : int
    {
        Any = 0,
        Disk = 1,
        Print = 2,
        Reserved = 8,
    }

    public enum ResourceDisplaytype : int
    {
        Generic = 0x0,
        Domain = 0x01,
        Server = 0x02,
        Share = 0x03,
        File = 0x04,
        Group = 0x05,
        Network = 0x06,
        Root = 0x07,
        Shareadmin = 0x08,
        Directory = 0x09,
        Tree = 0x0a,
        Ndscontainer = 0x0b
    }


}



