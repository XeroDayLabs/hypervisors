/*
    Icmp classes for C#
		Version: 1.0		Date: 2002/04/15
*/
/*
    Copyright � 2002, The KPD-Team
    All rights reserved.
    http://www.mentalis.org/

  Redistribution and use in source and binary forms, with or without
  modification, are permitted provided that the following conditions
  are met:

    - Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer. 

    - Neither the name of the KPD-Team, nor the names of its contributors
       may be used to endorse or promote products derived from this
       software without specific prior written permission. 

  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
  LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
  FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL
  THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
  INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
  (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
  SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
  HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
  STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
  ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
  OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// <summary>The System.Net namespace provides a simple programming interface for many of the protocols used on networks today.</summary>

namespace Org.Mentalis.Network
{
    /// <summary>
    /// Implements the ICMP messaging service.
    /// </summary>
    /// <remarks>Currently, the implementation only supports the echo message (better known as 'ping').</remarks>
    public class Icmp
    {
        private readonly AsyncCallback cb = new AsyncCallback(clientSocketData);

        /// <summary>
        /// Initializes an instance of the Icmp class.
        /// </summary>
        /// <param name="host">The host that will be used to communicate with.</param>
        public Icmp(IPAddress host)
        {
            Host = host;
        }

        /// <summary>
        /// Generates the Echo message to send.
        /// </summary>
        /// <returns>An array of bytes that represents the ICMP echo message to send.</returns>
        protected byte[] GetEchoMessageBuffer()
        {
            EchoMessage message = new EchoMessage();
            message.Type = 8; // ICMP echo
            message.Data = new Byte[1300]; // aliz modification: this is grossly oversized to prevent the socketException I'm seeing: "... the buffer used to receive a datagram into was smaller than the datagram itself"
            for (int i = 0; i < 32; i++)
            {
                message.Data[i] = 32; // Send spaces
            }
            message.CheckSum = message.GetChecksum();
            return message.GetObjectBytes();
        }

        /// <summary>
        /// Initiates an ICMP ping with a timeout of 1000 milliseconds.
        /// </summary>
        /// <exception cref="SocketException">There was an error while communicating with the remote server.</exception>
        /// <returns>A TimeSpan object that holds the time it takes for a packet to travel to the remote server and back. A value of TimeSpan.MaxValue indicates a timeout.</returns>
        /// <example>
        /// The following example will ping the server www.mentalis.org ten times and print the results in the Console.
        /// <c>
        /// <pre>
        /// Icmp icmp = new Icmp(Dns.Resolve("www.mentalis.org").AddressList[0]);
        /// for (int i = 0; i &lt; 10; i++) {
        /// 	Console.WriteLine(icmp.Ping().TotalMilliseconds);
        /// }
        /// </pre>
        /// </c>
        /// </example>
        public TimeSpan Ping()
        {
            return Ping(1000);
        }

        public TimeSpan Ping(TimeSpan timeout)
        {
            return Ping((int) timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Initiates an ICMP ping.
        /// </summary>
        /// <param name="timeout">Specifies the timeout in milliseconds. If this value is set to Timeout.Infinite, the method will never time out.</param>
        /// <returns>A TimeSpan object that holds the time it takes for a packet to travel to the remote server and back. A value of TimeSpan.MaxValue indicates a timeout.</returns>
        public TimeSpan Ping(int timeout)
        {
            EndPoint remoteEP = new IPEndPoint(Host, 0);
            using (Socket ClientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp))
            {
                byte[] buffer = GetEchoMessageBuffer();
                StartTime = DateTime.Now;
                // Send the ICMP message and receive the reply
                // I reaaaaaallly hate this design in the framework - there's an overload of Socket.send and socket.Recieve which 
                // will return an error code on failure, instead of throwing, but there is no analogue for the .SendTo and .ReceiveFrom
                // methods. Because of this, we need to try/catch, and just ignore the "buffer too small" exceptions D:
                while (true)
                {
                    try
                    {
                        ClientSocket.SendTo(buffer, remoteEP);
                        break;
                    }
                    catch (SocketException se)
                    {
                        if (se.ErrorCode == 10055)
                        {
                            // This is "An operation on a socket could not be performed because the system lacked sufficient 
                            // buffer space or because a queue was full."
                            // We'll retry after a short delay.
                            Thread.Sleep(TimeSpan.FromSeconds(3));
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
                buffer = new byte[buffer.Length + 20];
                socketDataResult res = new socketDataResult() {socket = ClientSocket};
                IAsyncResult hnd = ClientSocket.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, cb, res);

                if (!hnd.AsyncWaitHandle.WaitOne(timeout))
                {
                    // timed out!
                    hnd.AsyncWaitHandle.Close();
                    ClientSocket.Close();
                    return TimeSpan.MaxValue;
                }

                hnd.AsyncWaitHandle.Close();
                while (!res.recvCBFinished)
                    Thread.Yield();
                ClientSocket.Close();

                if (res.err != SocketError.Success && res.err != SocketError.MessageSize)
                    throw new SocketException((int) res.err);
                IcmpMessage response = IcmpMessage.fromBytes(buffer);
                if (!Equals(response.src, Host) || response.Type != 0 || response.Type != 0)
                    return TimeSpan.MaxValue;
            }
            return DateTime.Now.Subtract(StartTime);
        }

        private class socketDataResult
        {
            public SocketError err = SocketError.VersionNotSupported;
            public bool recvCBFinished = false;
            public Socket socket;
        }

        private static void clientSocketData(IAsyncResult ar)
        {
            socketDataResult res = (socketDataResult) ar.AsyncState;
            try
            {
                res.socket.EndReceive(ar, out res.err);
            }
            catch (ObjectDisposedException)
            {
                // Ugh, this is the only way to abort a BeginReceive :((
            }

            res.recvCBFinished = true;
        }

        /// <summary>
        /// Gets or sets the address of the remote host to communicate with.
        /// </summary>
        /// <value>An IPAddress instance that specifies the address of the remote host to communicate with.</value>
        /// <exception cref="ArgumentNullException">The specified value is null (Nothing in VB.NET).</exception>
        protected IPAddress Host
        {
            get { return m_Host; }
            set
            {
                if (value == null)
                    throw new ArgumentNullException();
                m_Host = value;
            }
        }

        /// <summary>
        /// Gets or sets the time when the ping began/begins.
        /// </summary>
        /// <value>A DateTime object that specifies when the ping began.</value>
        protected DateTime StartTime
        {
            get { return m_StartTime; }
            set { m_StartTime = value; }
        }

        // Private variables
        /// <summary>Stores the value of the Host property.</summary>
        private IPAddress m_Host;

        /// <summary>Stores the value of the StartTime property.</summary>
        private DateTime m_StartTime;
    }

    /// <summary>
    /// Defines a base ICMP message
    /// </summary>
    public abstract class IcmpMessage
    {
        /// <summary>
        /// Initializes a new IcmpMessage instance.
        /// </summary>
        public IcmpMessage()
        {
        }

        /// <summary>
        /// Gets or sets the type of the message.
        /// </summary>
        /// <value>A byte that specifies the type of the message.</value>
        public byte Type
        {
            get { return m_Type; }
            set { m_Type = value; }
        }

        /// <summary>
        /// Gets or sets the message code.
        /// </summary>
        /// <value>A byte that specifies the message code.</value>
        public byte Code
        {
            get { return m_Code; }
            set { m_Code = value; }
        }

        /// <summary>
        /// Gets or sets the chacksum for this message.
        /// </summary>
        /// <value>An unsigned short that holds the checksum of this message.</value>
        public ushort CheckSum
        {
            get { return m_CheckSum; }
            set { m_CheckSum = value; }
        }

        /// <summary>
        /// Serializes the object into an array of bytes.
        /// </summary>
        /// <returns>An array of bytes that represents the ICMP message.</returns>
        public virtual byte[] GetObjectBytes()
        {
            byte[] ret = new byte[4];
            Array.Copy(BitConverter.GetBytes(Type), 0, ret, 0, 1);
            Array.Copy(BitConverter.GetBytes(Code), 0, ret, 1, 1);
            Array.Copy(BitConverter.GetBytes(CheckSum), 0, ret, 2, 2);
            return ret;
        }

        /// <summary>
        /// Calculates the checksum of this message.
        /// </summary>
        /// <returns>An unsigned short that holds the checksum of this ICMP message.</returns>
        public ushort GetChecksum()
        {
            ulong sum = 0;
            byte[] bytes = GetObjectBytes();
            // Sum all the words together, adding the final byte if size is odd
            int i;
            for (i = 0; i < bytes.Length - 1; i += 2)
            {
                sum += BitConverter.ToUInt16(bytes, i);
            }
            if (i != bytes.Length)
                sum += bytes[i];
            // Do a little shuffling
            sum = (sum >> 16) + (sum & 0xFFFF);
            sum += (sum >> 16);
            return (ushort) (~sum);
        }

        // Private variables
        /// <summary>Holds the value of the Type property.</summary>
        private byte m_Type = 0;

        /// <summary>Holds the value of the Code property.</summary>
        private byte m_Code = 0;

        /// <summary>Holds the value of the CheckSum property.</summary>
        private ushort m_CheckSum = 0;

        public IPAddress src;
        public IPAddress dst;

        public static IcmpMessage fromBytes(byte[] buffer)
        {
            IcmpMessage toRet = new EchoMessage();

            // I am so lazy today
            if (buffer[0] != 0x45 || buffer[9] != 0x01)
                return null;

            long srcDword = (buffer[0x0f] << 24) |
                            (buffer[0x0e] << 16) |
                            (buffer[0x0d] << 8) |
                            (buffer[0x0c]);
            long dstDword = (buffer[0x13] << 24) |
                            (buffer[0x12] << 16) |
                            (buffer[0x11] << 8) |
                            (buffer[0x10]);

            toRet.src = new IPAddress(srcDword & 0xffffffff);
            toRet.dst = new IPAddress(dstDword & 0xffffffff);

            toRet.Type = buffer[0x14];
            toRet.Code = buffer[0x15];
            toRet.CheckSum = (ushort) ((ushort) (buffer[0x16] << 8) + buffer[0x17]);

            return toRet;
        }
    }

    /// <summary>
    /// Defines an ICMP message with an ID and a sequence number.
    /// </summary>
    public class InformationMessage : IcmpMessage
    {
        /// <summary>
        /// Initializes a new InformationMessage instance.
        /// </summary>
        public InformationMessage()
        {
        }

        /// <summary>
        /// Gets or sets the identification number.
        /// </summary>
        /// <value>An unsigned short that holds the identification number of this message.</value>
        public ushort Identifier
        {
            get { return m_Identifier; }
            set { m_Identifier = value; }
        }

        /// <summary>
        /// Gets or sets the sequence number.
        /// </summary>
        /// <value>An unsigned short that holds the sequence number of this message.</value>
        public ushort SequenceNumber
        {
            get { return m_SequenceNumber; }
            set { m_SequenceNumber = value; }
        }

        /// <summary>
        /// Serializes the object into an array of bytes.
        /// </summary>
        /// <returns>An array of bytes that represents the ICMP message.</returns>
        public override byte[] GetObjectBytes()
        {
            byte[] ret = new byte[8];
            Array.Copy(base.GetObjectBytes(), 0, ret, 0, 4);
            Array.Copy(BitConverter.GetBytes(Identifier), 0, ret, 4, 2);
            Array.Copy(BitConverter.GetBytes(SequenceNumber), 0, ret, 6, 2);
            return ret;
        }

        // Private variables
        /// <summary>Holds the value of the Identifier property.</summary>
        private ushort m_Identifier = 0;

        /// <summary>Holds the value of the SequenceNumber property.</summary>
        private ushort m_SequenceNumber = 0;
    }

    /// <summary>
    /// Defines an echo ICMP message.
    /// </summary>
    public class EchoMessage : InformationMessage
    {
        /// <summary>
        /// Initializes a new EchoMessage instance.
        /// </summary>
        public EchoMessage()
        {
        }

        /// <summary>
        /// Gets or sets the data of this message.
        /// </summary>
        /// <value>An array of bytes that represents the data of this message.</value>
        public byte[] Data
        {
            get { return m_Data; }
            set { m_Data = value; }
        }

        /// <summary>
        /// Serializes the object into an array of bytes.
        /// </summary>
        /// <returns>An array of bytes that represents the ICMP message.</returns>
        public override byte[] GetObjectBytes()
        {
            int length = 8;
            if (Data != null)
                length += Data.Length;
            byte[] ret = new byte[length];
            Array.Copy(base.GetObjectBytes(), 0, ret, 0, 8);
            if (Data != null)
                Array.Copy(Data, 0, ret, 8, Data.Length);
            return ret;
        }

        // Private variables
        /// <summary>Holds the value of the Data property.</summary>
        private byte[] m_Data;
    }
}