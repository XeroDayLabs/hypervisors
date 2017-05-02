using System;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace hypervisors
{
    public class SSHExecutor : IRemoteExecution
    {
        private readonly string _hostIp;
        private readonly string _hostUsername;
        private readonly string _hostPassword;

        public SSHExecutor(string hostIP, string hostUsername, string hostPassword)
        {
            _hostIp = hostIP;
            _hostUsername = hostUsername;
            _hostPassword = hostPassword;
        }

        public void mkdir(string newDir)
        {
            throw new NotImplementedException();
        }

        public void copyToGuest(string srcpath, string dstpath)
        {
            throw new NotImplementedException();
        }

        public string getFileFromGuest(string srcpath)
        {
            throw new NotImplementedException();
        }

        public executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            if (workingDir != null)
                throw new NotSupportedException();

            ConnectionInfo inf = new ConnectionInfo(_hostIp, _hostUsername, new PasswordAuthenticationMethod(_hostUsername, _hostPassword));
            try
            {
                using (SshClient client = new SshClient(inf))
                {
                    SshCommand returnVal = client.RunCommand(string.Format("{0} {1}", toExecute, args));
                    return new executionResult(returnVal);
                }
            }
            catch (SshException)
            {
                throw new hypervisorExecutionException();
            }
        }

        public void startExecutableAsync(string toExecute, string args, string workingDir = null, string stdoutfilename = null, string stderrfilename = null, string retCodeFilename = null)
        {
            throw new NotImplementedException();
        }

        public void testConnectivity()
        {
            executionResult res = startExecutable("echo", "teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();
        }
    }
}