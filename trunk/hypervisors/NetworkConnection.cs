using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace hypervisors
{
    /// <summary>
    /// This came from stackoverflow, with some bugfixes added
    /// </summary>
    public class NetworkConnection : IDisposable
    {
        private string _networkName;

        private static ConcurrentDictionary<string, bool> openConnections = new ConcurrentDictionary<string, bool>();

        public NetworkConnection(string networkName, NetworkCredential credentials, out Exception e)
        {
            e = null;
            _networkName = networkName;

            if (!openConnections.TryAdd(_networkName, true))
                throw new Exception("Duplicate NetworkConnection classes for network name '" + _networkName + "'");

            NetResource netResource = new NetResource()
            {
                Scope = ResourceScope.GlobalNetwork,
                ResourceType = ResourceType.Disk,
                DisplayType = ResourceDisplaytype.Share,
                RemoteName = networkName
            };

            string userName = string.IsNullOrEmpty(credentials.Domain)
                ? credentials.UserName
                : string.Format(@"{0}\{1}", credentials.Domain, credentials.UserName);

            int result = WNetAddConnection2(netResource, credentials.Password, userName, 0);

            // If this returns 1219 - ERROR_SESSION_CREDENTIAL_CONFLICT - then we must disconnect the share and retry.
            if (result == 1219)
            {
                WNetCancelConnection2(_networkName, 0, true);
                result = WNetAddConnection2(netResource, credentials.Password, userName, 0);
            }

            if (result != 0)
            {
                e = new Win32Exception(result, "Error connecting to remote share, GLE " + Marshal.GetLastWin32Error());
                bool foo;
                if (!openConnections.TryRemove(_networkName, out foo))
                {
                    string tmp = _networkName;
                    _networkName = null;
                    throw new Exception("Couldn't remove network credential '" + tmp + "'");
                }
                _networkName = null;
            }
        }

        ~NetworkConnection()
        {
            try
            {
                destroyConnection();
            }
            catch (Exception)
            {
                // :(
            }
        }

        public void Dispose()
        {
            destroyConnection();
            GC.SuppressFinalize(this);
        }

        protected virtual void destroyConnection()
        {
            if (_networkName != null)
            {
                WNetCancelConnection2(_networkName, 0, true);
                bool foo;
                if (!openConnections.TryRemove(_networkName, out foo))
                {
                    // Oh no! We can't safely throw from the finalizer thread, how can we notify the user?!
                    openConnections = null; // >:)
                }
                _networkName = null;
            }
        }

        [DllImport("mpr.dll", SetLastError = true)]
        private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

        [DllImport("mpr.dll", SetLastError = true)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);
    }
}