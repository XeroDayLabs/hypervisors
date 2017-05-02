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

            KeyboardInteractiveAuthenticationMethod auth = new KeyboardInteractiveAuthenticationMethod(_hostUsername);
            auth.AuthenticationPrompt += authCB;

            ConnectionInfo inf = new ConnectionInfo(_hostIp, _hostUsername, auth);
            try
            {
                using (SshClient client = new SshClient(inf))
                {
                    client.Connect();

                    SshCommand returnVal = client.RunCommand(string.Format("{0} {1}", toExecute, args));
                    return new executionResult(returnVal);
                }
            }
            catch (SshException)
            {
                throw new hypervisorExecutionException();
            }
        }

        private void authCB(object sender, AuthenticationPromptEventArgs e)
        {
            foreach (AuthenticationPrompt prompt in e.Prompts)
            {
                if (prompt.Request.IndexOf("Password:", StringComparison.InvariantCultureIgnoreCase) != -1)
                    prompt.Response = _hostPassword;
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