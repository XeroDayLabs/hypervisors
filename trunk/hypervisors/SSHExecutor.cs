using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace hypervisors
{
    public class SSHExecutor : remoteExecution
    {
        private readonly string _hostIp;
        private string _hostUsername;
        private readonly string _hostPassword;
        private ConnectionInfo inf;

        public SSHExecutor(string hostIP, string hostUsername, string hostPassword)
        {
            _hostIp = hostIP;
            _hostUsername = hostUsername;

            // We assume that if 'password' begins with this magic, then we should do keypair auth.
            if (hostPassword.Trim().ToUpper().StartsWith("-----BEGIN RSA PRIVATE KEY-----") ||
                hostPassword.Trim().ToUpper().StartsWith("-----BEGIN DSA PRIVATE KEY-----"))
            {
                // This is a huge bodge, but FreeNAS 11 won't let me do password auth as root, even when I enable it in the UI and faff.
                // Because of this, need a quick way to support keypair auth.
                using (MemoryStream mem = new MemoryStream(Encoding.ASCII.GetBytes(hostPassword)))
                {
                    inf = new ConnectionInfo(_hostIp, _hostUsername, new AuthenticationMethod[]
                {
                    new PrivateKeyAuthenticationMethod(_hostUsername, new PrivateKeyFile[]
                    {
                        new PrivateKeyFile(mem),
                    }),
                });
                }
            }
            else
            {
                // Otherwise, we do password auth.
                // VMWare ESXi is configured to deny password auth but permit keyboard-interactive auth out of the box, so we support
                // this and fallback to password auth if needed.
                KeyboardInteractiveAuthenticationMethod interactiveAuth = new KeyboardInteractiveAuthenticationMethod(_hostUsername);
                interactiveAuth.AuthenticationPrompt += authCB;
                // Keyboard auth is the only supported scheme for the iLos.
                _hostPassword = hostPassword;
                PasswordAuthenticationMethod passwordAuth = new PasswordAuthenticationMethod(_hostUsername, hostPassword);
                inf = new ConnectionInfo(_hostIp, _hostUsername, interactiveAuth, passwordAuth);
            }
        }

        public override void mkdir(string newDir, cancellableDateTime deadline)
        {
            throw new NotImplementedException();
        }

        public override void copyToGuest(string dstpath, string srcpath, cancellableDateTime deadline = null)
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

        public override string tryGetFileFromGuest(string srcpath, out Exception errorOrNull)
        {
            try
            {
                using (SftpClient client = new SftpClient(inf))
                {
                    client.Connect();

                    string toRet = client.ReadAllText(srcpath);
                    errorOrNull = null;
                    return toRet;
                }
            }
            catch (Exception e)
            {
                errorOrNull = e;
                return null;
            }
        }

        public override void deleteFile(string toDelete, cancellableDateTime deadline)
        {
            throw new NotImplementedException();
        }

        public override void Dispose()
        {
            
        }

        public override executionResult startExecutable(string toExecute, string args, string workingDir = null, cancellableDateTime deadline = null)
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

        public override IAsyncExecutionResult startExecutableAsyncInteractively(string cmdExe, string args, string workingDir)
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