using System;
using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace hypervisors
{
    public class SSHExecutor : remoteExecution
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

        public override void mkdir(string newDir)
        {
            throw new NotImplementedException();
        }

        public override void copyToGuest(string srcpath, string dstpath)
        {
            throw new NotImplementedException();
        }

        public override string getFileFromGuest(string srcpath)
        {
            throw new NotImplementedException();
        }

        public executionResult startExecutable(string toExecute, string args, string workingDir = null)
        {
            if (workingDir != null)
                throw new NotSupportedException();

            // VMWare ESXi is configured to deny password auth but permit keyboard-interactive auth out of the box, so we support
            // this and fallback to password auth if needed.
            KeyboardInteractiveAuthenticationMethod interactiveAuth = new KeyboardInteractiveAuthenticationMethod(_hostUsername);
            interactiveAuth.AuthenticationPrompt += authCB;
            // Keyboard auth is the only supported scheme for the iLos.
            PasswordAuthenticationMethod passwordAuth = new PasswordAuthenticationMethod(_hostUsername, _hostPassword);

            ConnectionInfo inf = new ConnectionInfo(_hostIp, _hostUsername, interactiveAuth, passwordAuth);
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

        public override IAsyncExecutionResult startExecutableAsync(string toExecute, string args, string workingDir = null)
        {
            throw new NotImplementedException();
        }

        public override void testConnectivity()
        {
            executionResult res = startExecutable("echo", "teststring");
            if (!res.stdout.Contains("teststring"))
                throw new hypervisorExecutionException_retryable();
        }
    }
}