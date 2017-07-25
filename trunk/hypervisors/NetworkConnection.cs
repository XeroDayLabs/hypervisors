﻿using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace hypervisors
{
    /// <summary>
    /// ty based stackoverflow
    /// </summary>
    public class NetworkConnection : IDisposable
    {
        readonly string _networkName;

        public NetworkConnection(string networkName,
            NetworkCredential credentials)
        {
            _networkName = networkName;

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
                throw new Win32Exception(result, "Error connecting to remote share, GLE " + Marshal.GetLastWin32Error());
            }
        }

        ~NetworkConnection()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            WNetCancelConnection2(_networkName, 0, true);
        }

        [DllImport("mpr.dll", SetLastError = true)]
        private static extern int WNetAddConnection2(NetResource netResource, string password, string username, int flags);

        [DllImport("mpr.dll", SetLastError = true)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);
    }
}