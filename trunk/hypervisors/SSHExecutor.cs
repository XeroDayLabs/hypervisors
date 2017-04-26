using System;
using System.Threading;
using Tamir.SharpSsh;
using Tamir.SharpSsh.jsch;

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

            SshExec exec = null;
            try
            {
                exec = new SshExec(_hostIp, _hostUsername, _hostPassword);
                exec.Connect();

                string returnVal = exec.RunCommand(string.Format("{0} {1}", toExecute, args));
                return new executionResult() { stdout =  returnVal };
            }
            catch (JSchException)
            {
                throw new hypervisorExecutionException();
            }
            finally
            {
                if (exec != null)
                    exec.Close();
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