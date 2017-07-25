using System;
using System.Diagnostics;
using System.IO;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace hypervisors
{
    public class SSHExecutor : remoteExecution
    {
        private readonly string _hostIp;
        private readonly string _hostUsername;
        private readonly string _hostPassword;
        private readonly ConnectionInfo inf;

        public SSHExecutor(string hostIP, string hostUsername, string hostPassword)
        {
            _hostIp = hostIP;
            _hostUsername = hostUsername;
            _hostPassword = hostPassword;

            // VMWare ESXi is configured to deny password auth but permit keyboard-interactive auth out of the box, so we support
            // this and fallback to password auth if needed.
            KeyboardInteractiveAuthenticationMethod interactiveAuth = new KeyboardInteractiveAuthenticationMethod(_hostUsername);
            interactiveAuth.AuthenticationPrompt += authCB;
            // Keyboard auth is the only supported scheme for the iLos.
            PasswordAuthenticationMethod passwordAuth = new PasswordAuthenticationMethod(_hostUsername, _hostPassword);
            inf = new ConnectionInfo(_hostIp, _hostUsername, interactiveAuth, passwordAuth);
        }

        public override void mkdir(string newDir)
        {
            throw new NotImplementedException();
        }

        public override void copyToGuest(string dstpath, string srcpath)
        {
            if (!File.Exists(srcpath))
                throw new Exception("src file not found");

            using (SftpClient client = new SftpClient(inf))
            {
                client.Connect();
                byte[] bytesToWrite = File.ReadAllBytes(srcpath);
                client.WriteAllBytes(dstpath, bytesToWrite);
            }
        }

        public override string getFileFromGuest(string srcpath)
        {
            using (SftpClient client = new SftpClient(inf))
            {
                client.Connect();

                return client.ReadAllText(srcpath);
            }
        }

        public override void deleteFile(string toDelete)
        {
            throw new NotImplementedException();
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null, TimeSpan timeout = default(TimeSpan))
        {
            if (workingDir != null)
                throw new NotSupportedException();

            // TODO: timeouts aren't supported here, they're just ignored.

            try
            {
                using (SshClient client = new SshClient(inf))
                {
                    client.Connect();

                    SshCommand returnVal = client.RunCommand(string.Format("{0} {1}", toExecute, args));

                    Debug.WriteLine("{0} : Command '{1}' args '{2}' retcode {3} stdout {4} stderr {5}", _hostIp, toExecute, args, returnVal.ExitStatus, returnVal.Result, returnVal.Error);

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