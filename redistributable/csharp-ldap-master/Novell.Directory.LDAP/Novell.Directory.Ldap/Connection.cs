/******************************************************************************
* The MIT License
* Copyright (c) 2003 Novell Inc.  www.novell.com
* 
* Permission is hereby granted, free of charge, to any person obtaining  a copy
* of this software and associated documentation files (the Software), to deal
* in the Software without restriction, including  without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
* copies of the Software, and to  permit persons to whom the Software is 
* furnished to do so, subject to the following conditions:
* 
* The above copyright notice and this permission notice shall be included in 
* all copies or substantial portions of the Software.
* 
* THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*******************************************************************************/
//
// Novell.Directory.Ldap.Connection.cs
//
// Author:
//   Sunil Kumar (Sunilk@novell.com)
//
// (C) 2003 Novell, Inc (http://www.novell.com)
//

using log4net;
using Mono.Security.Protocol.Tls;
using Novell.Directory.Ldap.Asn1;
using Novell.Directory.Ldap.Rfc2251;
using Novell.Directory.Ldap.Utilclass;
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Syscert = System.Security.Cryptography.X509Certificates;

namespace Novell.Directory.Ldap
{
    /// <summary> The class that creates a connection to the Ldap server. After the
    /// connection is made, a thread is created that reads data from the
    /// connection.
    /// 
    /// The application's thread sends a request to the MessageAgent class, which
    /// creates a Message class.  The Message class calls the writeMessage method
    /// of this class to send the request to the server. The application thread
    /// will then query the MessageAgent class for a response.
    /// 
    /// The reader thread multiplexes response messages received from the
    /// server to the appropriate Message class. Each Message class
    /// has its own message queue.
    /// 
    /// Unsolicited messages are process separately, and if the application
    /// has registered a handler, a separate thread is created for that
    /// application's handler to process the message.
    /// 
    /// Note: the reader thread must not be a "selfish" thread, since some
    /// operating systems do not time slice.
    /// 
    /// </summary>
    /*package*/
    sealed class Connection : IDisposable
    {
        public event CertificateValidationCallback OnCertificateValidation;
        public enum CertificateProblem : long
        {
            CertEXPIRED = 0x800B0101,
            CertVALIDITYPERIODNESTING = 0x800B0102,
            CertROLE = 0x800B0103,
            CertPATHLENCONST = 0x800B0104,
            CertCRITICAL = 0x800B0105,
            CertPURPOSE = 0x800B0106,
            CertISSUERCHAINING = 0x800B0107,
            CertMALFORMED = 0x800B0108,
            CertUNTRUSTEDROOT = 0x800B0109,
            CertCHAINING = 0x800B010A,
            CertREVOKED = 0x800B010C,
            CertUNTRUSTEDTESTROOT = 0x800B010D,
            CertREVOCATION_FAILURE = 0x800B010E,
            CertCN_NO_MATCH = 0x800B010F,
            CertWRONG_USAGE = 0x800B0110,
            CertUNTRUSTEDCA = 0x800B0112
        }

        private static ILog log = LogManager.GetLogger(typeof(Connection));

        private static string GetProblemMessage(CertificateProblem problem)
        {
            string problemCodeName = CertificateProblem.GetName(typeof(CertificateProblem), problem);
            return problemCodeName != null ? problemCodeName : "Unknown Certificate Problem";
        }

        private ArrayList handshakeProblemsEncountered = new ArrayList();
        private void InitBlock()
        {
            writeSemaphore = new Object();
            encoder = new LBEREncoder();
            decoder = new LBERDecoder();
            stopReaderMessageID = CONTINUE_READING;
            messages = new MessageVector(5, 5);
            unsolicitedListeners = new ArrayList(3);
        }

        internal bool Ssl
        {
            get
            {
                return ssl;
            }

            set
            {
                ssl = value;
            }
        }

        /// <summary> gets the host used for this connection</summary>
        internal string Host
        {
            /* package */

            get
            {
                return host;
            }
        }

        /// <summary> gets the port used for this connection</summary>
        internal int Port
        {
            /* package */
            get
            {
                return port;
            }
        }

        /// <summary> gets the writeSemaphore id used for active bind operation</summary>
        /// <summary> sets the writeSemaphore id used for active bind operation</summary>
        internal int BindSemId
        {
            /* package */
            get
            {
                return bindSemaphoreId;
            }

            /* package */
            set
            {
                bindSemaphoreId = value;
            }
        }

        /// <summary> checks if the writeSemaphore id used for active bind operation is clear</summary>
        internal bool BindSemIdClear
        {
            /* package */
            get
            {
                return bindSemaphoreId == 0;
            }
        }

        /// <summary> Return whether the application is bound to this connection.
        /// Note: an anonymous bind returns false - not bound
        /// </summary>
        internal bool Bound
        {
            /* package */

            get
            {
                if (bindProperties != null)
                {
                    // Bound if not anonymous
                    return (!bindProperties.Anonymous);
                }
                return false;
            }
        }

        /// <summary> Return whether a connection has been made</summary>
        internal bool Connected
        {
            /* package */
            get
            {
                return (in_Renamed != null);
            }
        }
        /// <summary> 
        /// Sets the authentication credentials in the object
        /// and set flag indicating successful bind.
        /// 
        /// 
        /// 
        /// </summary>
        /// <returns>  The BindProperties object for this connection.
        /// </returns>
        /// <summary> 
        /// Sets the authentication credentials in the object
        /// and set flag indicating successful bind.
        /// 
        /// 
        /// 
        /// </summary>
        /// <param name="bindProps">  The BindProperties object to set.
        /// </param>
        internal BindProperties BindProperties
        {
            /* package */
            get
            {
                return bindProperties;
            }

            /* package */
            set
            {
                bindProperties = value;
            }
        }

        /// <summary> Gets the current referral active on this connection if created to
        /// follow referrals.
        /// 
        /// </summary>
        /// <returns> the active referral url
        /// </returns>
        /// <summary> Sets the current referral active on this connection if created to
        /// follow referrals.
        /// </summary>
        internal ReferralInfo ActiveReferral
        {
            get
            {
                return activeReferral;
            }

            set
            {
                activeReferral = value;
            }
        }

        private Object writeSemaphore;
        private int writeSemaphoreOwner = 0;
        private int writeSemaphoreCount = 0;

        // We need a message number for disconnect to grab the semaphore,
        // but may not have one, so we invent a unique one.
        private int ephemeralId = -1;
        private BindProperties bindProperties = null;
        private int bindSemaphoreId = 0; // 0 is never used by to lock a semaphore

        private Thread reader = null; // New thread that reads data from the server.
        private Thread deadReader = null; // Identity of last reader thread
        private IOException deadReaderException = null; // Last exception of reader

        private LBEREncoder encoder;
        private LBERDecoder decoder;

        /*
        * socket is the current socket being used.
        * nonTLSBackup is the backup socket if startTLS is called.
        * if nonTLSBackup is null then startTLS has not been called,
        * or stopTLS has been called to end TLS protection
        */
        private Socket sock = null;
        private TcpClient socket = null;
        private TcpClient nonTLSBackup = null;

        private Stream in_Renamed = null;
        private Stream out_Renamed = null;
        // When set to true the client connection is up and running
        private bool clientActive = true;

        private bool ssl = false;

        // Indicates we have received a server shutdown unsolicited notification
        private bool unsolSvrShutDnNotification = false;

        //  Ldap message IDs are all positive numbers so we can use negative
        //  numbers as flags.  This are flags assigned to stopReaderMessageID
        //  to tell the reader what state we are in.
        private const int CONTINUE_READING = -99;
        private const int STOP_READING = -98;

        //  Stops the reader thread when a Message with the passed-in ID is read.
        //  This parameter is set by stopReaderOnReply and stopTLS
        private int stopReaderMessageID;


        // Place to save message information classes
        private MessageVector messages;

        // Connection created to follow referral
        private ReferralInfo activeReferral = null;

        // Place to save unsolicited message listeners
        private ArrayList unsolicitedListeners;

        // The LdapSocketFactory to be used as the default to create new connections
        //		private static LdapSocketFactory socketFactory = null;
        // The LdapSocketFactory used for this connection
        //		private LdapSocketFactory mySocketFactory;
        private string host = null;
        private int port = 0;
        // Number of clones in addition to original LdapConnection using this
        // connection.
        private int cloneCount = 0;

        // These attributes can be retreived using the getProperty
        // method in LdapConnection.  Future releases might require
        // these to be local variables that can be modified using
        // the setProperty method.
        /* package */
        internal const string sdk = "2.2.2";
        /* package */
        internal const int protocol = 3;
        /* package */
        internal const string security = "simple";

        /// <summary> Create a new Connection object
        /// 
        /// </summary>
        /// <param name="factory">specifies the factory to use to produce SSL sockets.
        /// </param>
        /* package */
        //		internal Connection(LdapSocketFactory factory)
        internal Connection()
        {
            InitBlock();
            return;
        }

        /// <summary> Copy this Connection object.
        /// 
        /// This is not a true clone, but creates a new object encapsulating
        /// part of the connection information from the original object.
        /// The new object will have the same default socket factory,
        /// designated socket factory, host, port, and protocol version
        /// as the original object.
        /// The new object is NOT be connected to the host.
        /// 
        /// </summary>
        /// <returns> a shallow copy of this object
        /// </returns>
        /* package */
        internal object copy()
        {
            Connection c = new Connection();
            c.host = this.host;
            c.port = this.port;
            return c;
        }

        /// <summary> Acquire a simple counting semaphore that synchronizes state affecting
        /// bind. This method generates an ephemeral message id (negative number).
        /// 
        /// We bind using the message ID because a different thread may unlock
        /// the semaphore than the one that set it.  It is cleared when the
        /// response to the bind is processed, or when the bind operation times out.
        /// 
        /// Returns when the semaphore is acquired
        /// 
        /// </summary>
        /// <returns> the ephemeral message id that identifies semaphore's owner
        /// </returns>
        /* package */
        internal int acquireWriteSemaphore()
        {
            return acquireWriteSemaphore(0);
        }

        /// <summary> Acquire a simple counting semaphore that synchronizes state affecting
        /// bind. The semaphore is held by setting a value in writeSemaphoreOwner.
        /// 
        /// We bind using the message ID because a different thread may unlock
        /// the semaphore than the one that set it.  It is cleared when the
        /// response to the bind is processed, or when the bind operation times out.
        /// Returns when the semaphore is acquired.
        /// 
        /// </summary>
        /// <param name="msgId">a value that identifies the owner of this semaphore. A
        /// value of zero means assign a unique semaphore value.
        /// 
        /// </param>
        /// <returns> the semaphore value used to acquire the lock
        /// </returns>
        /* package */
        internal int acquireWriteSemaphore(int msgId)
        {
            int id = msgId;
            lock (writeSemaphore)
            {
                if (id == 0)
                {
                    ephemeralId = ((ephemeralId == Int32.MinValue) ? (ephemeralId = -1) : --ephemeralId);
                    id = ephemeralId;
                }
                while (true)
                {
                    if (writeSemaphoreOwner == 0)
                    {
                        // we have acquired the semahpore
                        writeSemaphoreOwner = id;
                        break;
                    }
                    else
                    {
                        if (writeSemaphoreOwner == id)
                        {
                            // we already own the semahpore
                            break;
                        }
                        try
                        {
                            // Keep trying for the lock
                            Monitor.Wait(writeSemaphore);
                            continue;
                        }
                        catch (ThreadInterruptedException)
                        {
                            // Keep trying for the lock
                            continue;
                        }
                    }
                }
                writeSemaphoreCount++;
            }
            return id;
        }

        /// <summary> Release a simple counting semaphore that synchronizes state affecting
        /// bind.  Frees the semaphore when number of acquires and frees for this
        /// thread match.
        /// 
        /// </summary>
        /// <param name="msgId">a value that identifies the owner of this semaphore
        /// </param>
        /* package */
        internal void freeWriteSemaphore(int msgId)
        {
            lock (writeSemaphore)
            {
                if (writeSemaphoreOwner == 0)
                {
                    throw new SystemException("Connection.freeWriteSemaphore(" + msgId + "): semaphore not owned by any thread");
                }
                else if (writeSemaphoreOwner != msgId)
                {
                    throw new SystemException("Connection.freeWriteSemaphore(" + msgId + "): thread does not own the semaphore, owned by " + writeSemaphoreOwner);
                }
                // if all instances of this semaphore for this thread are released,
                // wake up all threads waiting.
                if (--writeSemaphoreCount == 0)
                {
                    writeSemaphoreOwner = 0;
                    Monitor.Pulse(writeSemaphore);
                }
            }
        }

        /*
        * Wait until the reader thread ID matches the specified parameter.
        * Null = wait for the reader to terminate
        * Non Null = wait for the reader to start
        * Returns when the ID matches, i.e. reader stopped, or reader started.
        *
        * @param the thread id to match
        */
        private void waitForReader(Thread thread)
        {
            // wait for previous reader thread to terminate
            Thread rInst;
            Thread tInst;
            if (reader != null)
            {
                rInst = reader;
            }
            else
            {
                rInst = null;
            }

            if (thread != null)
            {
                tInst = thread;
            }
            else
            {
                tInst = null;
            }
            while (!Object.Equals(rInst, tInst))
            {
                // Don't initialize connection while previous reader thread still
                // active.
                try
                {
                    /*
                    * The reader thread may start and immediately terminate.
                    * To prevent the waitForReader from waiting forever
                    * for the dead to rise, we leave traces of the deceased.
                    * If the thread is already gone, we throw an exception.
                    */
                    if (thread == deadReader)
                    {
                        if (thread == null)
                            /* then we wanted a shutdown */
                            return;
                        IOException lex = deadReaderException;
                        deadReaderException = null;
                        deadReader = null;
                        // Reader thread terminated
                        throw new LdapException(ExceptionMessages.CONNECTION_READER, LdapException.CONNECT_ERROR, null, lex);
                    }
                    lock (this)
                    {
                        Monitor.Wait(this, TimeSpan.FromMilliseconds(5));
                    }
                }
                catch (ThreadInterruptedException)
                {
                }
                if (reader != null)
                {
                    rInst = reader;
                }
                else
                {
                    rInst = null;
                }

                if (thread != null)
                {
                    tInst = thread;
                }
                else
                {
                    tInst = null;
                }

            }
            deadReaderException = null;
            deadReader = null;
        }

        /// <summary> Constructs a TCP/IP connection to a server specified in host and port.
        /// 
        /// </summary>
        /// <param name="host">The host to connect to.
        /// 
        /// </param>
        /// <param name="port">The port on the host to connect to.
        /// </param>
        /* package */
        internal void connect(string host, int port)
        {
            connect(host, port, 0);
        }


        /****************************************************************************/
        public bool ServerCertificateValidation(
            Syscert.X509Certificate certificate,
            int[] certificateErrors)
        {
            if (null != OnCertificateValidation)
            {
                return OnCertificateValidation(certificate, certificateErrors);
            }

            return DefaultCertificateValidationHandler(certificate, certificateErrors);
        }

        public bool DefaultCertificateValidationHandler(
            Syscert.X509Certificate certificate,
            int[] certificateErrors)
        {
            bool retFlag = false;

            if (certificateErrors != null &&
                certificateErrors.Length > 0)
            {
                if (certificateErrors.Length == 1 && certificateErrors[0] == -2146762481)
                {
                    retFlag = true;
                }
                else
                {
                    log.Debug("Detected errors in the Server Certificate:");
                    for (int i = 0; i < certificateErrors.Length; i++)
                    {
                        handshakeProblemsEncountered.Add((CertificateProblem)((uint)certificateErrors[i]));
                        log.Debug(certificateErrors[i]);
                    }
                    retFlag = false;
                }
            }
            else
            {
                retFlag = true;
            }

            // Skip the server cert errors.
            return retFlag;
        }


        /***********************************************************************/
        /// <summary> Constructs a TCP/IP connection to a server specified in host and port.
        /// Starts the reader thread.
        /// 
        /// </summary>
        /// <param name="host">The host to connect to.
        /// 
        /// </param>
        /// <param name="port">The port on the host to connect to.
        /// 
        /// </param>
        /// <param name="semaphoreId">The write semaphore ID to use for the connect
        /// </param>
        private void connect(string host, int port, int semaphoreId)
        {
            /* Synchronized so all variables are in a consistant state and
            * so that another thread isn't doing a connect, disconnect, or clone
            * at the same time.
            */
            // Wait for active reader to terminate
            waitForReader(null);

            // Clear the server shutdown notification flag.  This should already
            // be false unless of course we are reusing the same Connection object
            // after a server shutdown notification
            unsolSvrShutDnNotification = false;

            int semId = acquireWriteSemaphore(semaphoreId);
            try
            {

                // Make socket connection to specified host and port
                if (port == 0)
                {
                    port = LdapConnection.DEFAULT_PORT;
                }

                try
                {
                    if ((in_Renamed == null) || (out_Renamed == null))
                    {
                        if (Ssl)
                        {
                            this.host = host;
                            this.port = port;
                            this.sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
                            IPAddress hostadd = Dns.GetHostEntry(host).AddressList[0];
                            IPEndPoint ephost = new IPEndPoint(hostadd, port);
                            sock.Connect(ephost);
                            NetworkStream nstream = new NetworkStream(sock, true);


                            SslClientStream sslstream = new SslClientStream(
                                                nstream,
                                                host,
                                                false,
                                                Mono.Security.Protocol.Tls.SecurityProtocolType.Ssl3 | Mono.Security.Protocol.Tls.SecurityProtocolType.Tls);
                            sslstream.ServerCertValidationDelegate += new CertificateValidationCallback(ServerCertificateValidation);
                            //						byte[] buffer = new byte[0];
                            //						sslstream.Read(buffer, 0, buffer.Length);
                            //						sslstream.doHandshake();												

                            in_Renamed = sslstream;
                            out_Renamed = sslstream;
                        }
                        else
                        {
                            socket = new TcpClient(host, port);
                            in_Renamed = socket.GetStream();
                            out_Renamed = socket.GetStream();
                        }
                    }
                    else
                    {
                        log.Debug("connect input/out Stream specified");
                    }
                }
                catch (SocketException se)
                {
                    // Unable to connect to server host:port
                    // freeWriteSemaphore(semId); 
                    sock = null;
                    socket = null;
                    throw new LdapException(ExceptionMessages.CONNECTION_ERROR, new Object[] { host, port }, LdapException.CONNECT_ERROR, null, se);
                }

                catch (IOException ioe)
                {
                    // Unable to connect to server host:port
                    // freeWriteSemaphore(semId);
                    sock = null;
                    socket = null;
                    throw new LdapException(ExceptionMessages.CONNECTION_ERROR, new Object[] { host, port }, LdapException.CONNECT_ERROR, null, ioe);
                }
                // Set host and port
                this.host = host;
                this.port = port;
                // start the reader thread
                this.startReader();
                clientActive = true; // Client is up
            }
            finally
            {
                freeWriteSemaphore(semId);
            }
        }

        /// <summary>  Increments the count of cloned connections</summary>
        /* package */
        internal void incrCloneCount()
        {
            lock (this)
            {
                cloneCount++;
            }
        }

        /// <summary> Destroys a clone of <code>LdapConnection</code>.
        /// 
        /// This method first determines if only one <code>LdapConnection</code>
        /// object is associated with this connection, i.e. if no clone exists.
        /// 
        /// If no clone exists, the socket is closed, and the current
        /// <code>Connection</code> object is returned.
        /// 
        /// If multiple <code>LdapConnection</code> objects are associated
        /// with this connection, i.e. clones exist, a {@link #copy} of the
        /// this object is made, but is not connected to any host. This
        /// disassociates that clone from the original connection.  The new
        /// <code>Connection</code> object is returned.
        /// 
        /// Only one destroyClone instance is allowed to run at any one time.
        /// 
        /// If the connection is closed, any threads waiting for operations
        /// on that connection will wake with an LdapException indicating
        /// the connection is closed.
        /// 
        /// </summary>
        /// <param name="apiCall"><code>true</code> indicates the application is closing the
        /// connection or or creating a new one by calling either the
        /// <code>connect</code> or <code>disconnect</code> methods
        /// of <code>LdapConnection</code>.  <code>false</code>
        /// indicates that <code>LdapConnection</code> is being finalized.
        /// 
        /// </param>
        /// <returns> a Connection object or null if finalizing.
        /// </returns>
        /* package */
        internal Connection destroyClone(bool apiCall)
        {
            lock (this)
            {
                Connection conn = this;

                if (cloneCount > 0)
                {
                    cloneCount--;
                    // This is a clone, set a new connection object.
                    if (apiCall)
                    {
                        conn = (Connection)this.copy();
                    }
                    else
                    {
                        conn = null;
                    }
                }
                else
                {
                    if (in_Renamed != null)
                    {
                        // Not a clone and connected
                        /*
                        * Either the application has called disconnect or connect
                        * resulting in the current connection being closed. If the
                        * application has any queues waiting on messages, we
                        * need wake these up so the application does not hang.
                        * The boolean flag indicates whether the close came
                        * from an API call or from the object being finalized.
                        */
                        InterThreadException notify = new InterThreadException(
                            apiCall ? ExceptionMessages.CONNECTION_CLOSED : ExceptionMessages.CONNECTION_FINALIZED,
                            null, LdapException.CONNECT_ERROR, null, null);
                        // Destroy old connection
                        Dispose(false, "destroy clone", 0, notify);
                    }
                }
                return conn;
            }
        }

        /// <summary> sets the default socket factory
        /// 
        /// </summary>
        /// <param name="factory">the default factory to set
        /// </param>
        /* package */
        /// <summary> gets the socket factory used for this connection
        /// 
        /// </summary>
        /// <returns> the default factory for this connection
        /// </returns>
        /* package */

        /// <summary> clears the writeSemaphore id used for active bind operation</summary>
        /* package */
        internal void clearBindSemId()
        {
            bindSemaphoreId = 0;
        }

        /// <summary> Writes an LdapMessage to the Ldap server over a socket.
        /// 
        /// </summary>
        /// <param name="info">the Message containing the message to write.
        /// </param>
        /* package */
        internal void writeMessage(Message info)
        {
            ExceptionMessages em = new ExceptionMessages();
            Object[][] contents = em.getContents();
            messages.Add(info);

            // For bind requests, if not connected, attempt to reconnect
            if (info.BindRequest && Connected == false && host != null)
            {
                connect(host, port, info.MessageID);
            }
            if (Connected == true)
            {
                LdapMessage msg = info.Request;
                writeMessage(msg);
                return;
            }
            else
            {
                int errorcount = 0;
                for (errorcount = 0; errorcount < contents.Length; errorcount++)
                    if (contents[errorcount][0] == "CONNECTION_CLOSED")
                        break;
                throw new LdapException(ExceptionMessages.CONNECTION_CLOSED, new object[] { host, port }, LdapException.CONNECT_ERROR, (String)contents[errorcount][1]);
            }
        }


        /// <summary> Writes an LdapMessage to the Ldap server over a socket.
        /// 
        /// </summary>
        /// <param name="msg">the message to write.
        /// </param>
        /* package */
        internal void writeMessage(LdapMessage msg)
        {
            int id;
            // Get the correct semaphore id for bind operations
            if (bindSemaphoreId == 0)
            {
                // Semaphore id for normal operations
                id = msg.MessageID;
            }
            else
            {
                // Semaphore id for sasl bind operations
                id = bindSemaphoreId;
            }
            Stream myOut = out_Renamed;

            acquireWriteSemaphore(id);
            try
            {
                if (myOut == null)
                {
                    throw new IOException("Output stream not initialized");
                }
                if (!(myOut.CanWrite))
                {
                    return;
                }
                sbyte[] ber = msg.Asn1Object.getEncoding(encoder);
                myOut.Write(SupportClass.ToByteArray(ber), 0, ber.Length);
                myOut.Flush();
            }
            catch (IOException ioe)
            {
                if ((msg.Type == LdapMessage.BIND_REQUEST) && ssl)
                {
                    string strMsg = "Following problem(s) occurred while establishing SSL based Connection : ";
                    if (handshakeProblemsEncountered.Count > 0)
                    {
                        strMsg += GetProblemMessage((CertificateProblem)handshakeProblemsEncountered[0]);
                        for (int nProbIndex = 1; nProbIndex < handshakeProblemsEncountered.Count; nProbIndex++)
                        {
                            strMsg += ", " + GetProblemMessage((CertificateProblem)handshakeProblemsEncountered[nProbIndex]);
                        }
                    }
                    else
                    {
                        strMsg += "Unknown Certificate Problem";
                    }
                    throw new LdapException(strMsg, new object[] { host, port }, LdapException.SSL_HANDSHAKE_FAILED, null, ioe);
                }
                /*
                * IOException could be due to a server shutdown notification which
                * caused our Connection to quit.  If so we send back a slightly
                * different error message.  We could have checked this a little
                * earlier in the method but that would be an expensive check each
                * time we send out a message.  Since this shutdown request is
                * going to be an infrequent occurence we check for it only when
                * we get an IOException.  shutdown() will do the cleanup.
                */
                if (clientActive)
                {
                    // We beliefe the connection was alive
                    if (unsolSvrShutDnNotification)
                    {
                        // got server shutdown
                        throw new LdapException(ExceptionMessages.SERVER_SHUTDOWN_REQ, new object[] { host, port }, LdapException.CONNECT_ERROR, null, ioe);
                    }

                    // Other I/O Exceptions on host:port are reported as is
                    throw new LdapException(ExceptionMessages.IO_EXCEPTION, new object[] { host, port }, LdapException.CONNECT_ERROR, null, ioe);
                }
            }
            finally
            {
                freeWriteSemaphore(id);
                handshakeProblemsEncountered.Clear();
            }
            return;
        }

        /// <summary> Returns the message agent for this msg ID</summary>
        /* package */
        internal MessageAgent getMessageAgent(int msgId)
        {
            Message info = messages.findMessageById(msgId);
            return info.MessageAgent;
        }

        /// <summary> Removes a Message class from the Connection's list
        /// 
        /// </summary>
        /// <param name="info">the Message class to remove from the list
        /// </param>
        /* package */
        internal void removeMessage(Message info)
        {
            SupportClass.VectorRemoveElement(messages, info);
        }

        /// <summary> Cleans up resources associated with this connection.</summary>
        ~Connection()
        {
            //shutdown("Finalize", 0, null); // earlier code
            Dispose(false, "Finalize", 0, null);
        }
        /// <summary> Cleans up resources associated with this connection.
        /// This method may be called by finalize() for the connection, or it may
        /// be called by LdapConnection.disconnect().
        /// Should not have a writeSemaphore lock in place, as deadlock can occur
        /// while abandoning connections.
        /// </summary>
        private void shutdown(string reason, int semaphoreId, InterThreadException notifyUser)
        {
            Message info = null;
            if (!clientActive)
            {
                return;
            }
            clientActive = false;
            while (true)
            {
                // remove messages from connection list and send abandon
                try
                {
                    object temp_object;
                    temp_object = messages[0];
                    messages.RemoveAt(0);
                    info = (Message)temp_object;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // No more messages
                    break;
                }
                info.Abandon(null, notifyUser); // also notifies the application
            }

            int semId = acquireWriteSemaphore(semaphoreId);
            // Now send unbind if socket not closed
            if ((bindProperties != null) && (out_Renamed != null) && (out_Renamed.CanWrite) && (!bindProperties.Anonymous))
            {
                try
                {
                    LdapMessage msg = new LdapUnbindRequest(null);
                    sbyte[] ber = msg.Asn1Object.getEncoding(encoder);
                    out_Renamed.Write(SupportClass.ToByteArray(ber), 0, ber.Length);
                    out_Renamed.Flush();
                    out_Renamed.Close();
                }
                catch (Exception)
                {
                    // don't worry about error
                }
            }
            bindProperties = null;
            if (socket != null || sock != null)
            {
                // Just before closing the sockets, abort the reader thread
                if ((reader != null) && (reason != "reader: thread stopping"))
                    reader.Abort();
                // Close the socket
                try
                {
                    if (Ssl)
                    {
                        sock.Shutdown(SocketShutdown.Both);
                        sock.Close();
                    }
                    else
                    {
                        if (in_Renamed != null)
                            in_Renamed.Close();
                        socket.Close();
                    }
                }
                catch (IOException)
                {
                    // ignore problem closing socket
                }
                socket = null;
                sock = null;
                in_Renamed = null;
                out_Renamed = null;

            }
            freeWriteSemaphore(semId);
        }

        //Adding code here earlier code
        public void Dispose()
        {
            Dispose(true, "Finalize", 0, null);
            GC.SuppressFinalize(this);
        }

        public void Dispose(bool disposing, string reason, int semaphoreId, InterThreadException notifyUser)
        {
            if (!disposing)
            {
                Message info = null;
                if (!clientActive)
                {
                    return;
                }
                clientActive = false;
                while (true)
                {
                    // remove messages from connection list and send abandon
                    try
                    {
                        Object temp_object;
                        temp_object = messages[0];
                        messages.RemoveAt(0);
                        info = (Message)temp_object;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // No more messages
                        break;
                    }
                    info.Abandon(null, notifyUser); // also notifies the application
                }

                int semId = acquireWriteSemaphore(semaphoreId);
                // Now send unbind if socket not closed
                if ((bindProperties != null) && (out_Renamed != null) && (out_Renamed.CanWrite) && (!bindProperties.Anonymous))
                {
                    try
                    {
                        LdapMessage msg = new LdapUnbindRequest(null);
                        sbyte[] ber = msg.Asn1Object.getEncoding(encoder);
                        out_Renamed.Write(SupportClass.ToByteArray(ber), 0, ber.Length);
                        out_Renamed.Flush();
                        out_Renamed.Close();
                    }
                    catch (Exception)
                    {
                        // don't worry about error
                    }
                }
                bindProperties = null;
                if (socket != null || sock != null)
                {
                    // Just before closing the sockets, abort the reader thread
                    if ((reader != null) && (reason != "reader: thread stopping"))
                        reader.Abort();
                    // Close the socket
                    try
                    {
                        if (in_Renamed != null)
                            in_Renamed.Close();

                        if (Ssl && out_Renamed != null)
                            out_Renamed.Close();

                        if (sock != null)
                        {
                            //sock.Shutdown(SocketShutdown.Both);
                            sock.Close();
                        }
                        if (socket != null)
                        {
                            socket.Close();
                        }
                    }
                    catch (IOException)
                    {
                        // ignore problem closing socket
                    }
                    socket = null;
                    sock = null;
                    in_Renamed = null;
                    out_Renamed = null;

                }
                freeWriteSemaphore(semId);
            }
        }

        /// <summary> This tests to see if there are any outstanding messages.  If no messages
        /// are in the queue it returns true.  Each message will be tested to
        /// verify that it is complete.
        /// <I>The writeSemaphore must be set for this method to be reliable!</I>
        /// 
        /// </summary>
        /// <returns> true if no outstanding messages
        /// </returns>
        /* package */
        internal bool areMessagesComplete()
        {
            object[] messages = this.messages.ObjectArray;
            int length = messages.Length;

            // Check if SASL bind in progress
            if (bindSemaphoreId != 0)
            {
                return false;
            }

            // Check if any messages queued
            if (length == 0)
            {
                return true;
            }

            for (int i = 0; i < length; i++)
            {
                if (((Message)messages[i]).Complete == false)
                    return false;
            }
            return true;
        }

        /// <summary> The reader thread will stop when a reply is read with an ID equal
        /// to the messageID passed in to this method.  This is used by
        /// LdapConnection.StartTLS.
        /// </summary>
        /* package */
        internal void stopReaderOnReply(int messageID)
        {
            this.stopReaderMessageID = messageID;
        }

        /// <summary>startReader
        /// startReader should be called when socket and io streams have been
        /// set or changed.  In particular after client.Connection.startTLS()
        /// It assumes the reader thread is not running.
        /// </summary>
        /* package */
        internal void startReader()
        {
            // Start Reader Thread
            Thread r = new Thread(new ThreadStart(new ReaderThread(this).Run));
            r.IsBackground = true; // If the last thread running, allow exit.
            r.Start();
            waitForReader(r);
        }

        /// <summary> Indicates if the conenction is using TLS protection
        ///
        /// Return true if using TLS protection
        /// </summary>
        internal bool TLS
        {
            get
            {
                return (this.nonTLSBackup != null);
            }
        }

        /// <summary> StartsTLS, in this package, assumes the caller has:
        /// 1) Acquired the writeSemaphore
        /// 2) Stopped the reader thread
        /// 3) checked that no messages are outstanding on this connection.
        /// 
        /// After calling this method upper layers should start the reader
        /// by calling startReader()
        /// 
        /// In the client.Connection, StartTLS assumes Ldap.LdapConnection will
        /// stop and start the reader thread.  Connection.StopTLS will stop
        /// and start the reader thread.
        /// </summary>
        /* package */
        internal void startTLS()
        {
            try
            {
                waitForReader(null);
                this.nonTLSBackup = this.socket;
                /*				this.sock = 	new Socket ( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
                                IPAddress hostadd = Dns.GetHostEntry(host).AddressList[0];
                                IPEndPoint ephost = new IPEndPoint(hostadd,port);
                                sock.Connect(ephost);
                */
                //				NetworkStream nstream = new NetworkStream(this.socket,true);


                SslClientStream sslstream = new SslClientStream(
                                    socket.GetStream(),
                                    host,
                                    false,
                                    Mono.Security.Protocol.Tls.SecurityProtocolType.Ssl3 | Mono.Security.Protocol.Tls.SecurityProtocolType.Tls);
                sslstream.ServerCertValidationDelegate = new CertificateValidationCallback(ServerCertificateValidation);
                this.in_Renamed = (Stream)sslstream;
                this.out_Renamed = (Stream)sslstream;
            }
            catch (IOException ioe)
            {
                this.nonTLSBackup = null;
                throw new LdapException("Could not negotiate a secure connection", LdapException.CONNECT_ERROR, null, ioe);
            }
            catch (Exception uhe)
            {
                this.nonTLSBackup = null;
                throw new LdapException("The host is unknown", LdapException.CONNECT_ERROR, null, uhe);
            }
        }

        /*
        * Stops TLS.
        *
        * StopTLS, in this package, assumes the caller has:
        *  1) blocked writing (acquireWriteSemaphore).
        *  2) checked that no messages are outstanding.
        *
        *  StopTLS Needs to do the following:
        *  1) close the current socket
        *      - This stops the reader thread
        *      - set STOP_READING flag on stopReaderMessageID so that
        *        the reader knows that the IOException is planned.
        *  2) replace the current socket with nonTLSBackup,
        *  3) and set nonTLSBackup to null;
        *  4) reset input and outputstreams
        *  5) start the reader thread by calling startReader
        *
        *  Note: Sun's JSSE doesn't allow the nonTLSBackup socket to be
        * used any more, even though autoclose was false: you get an IOException.
        * IBM's JSSE hangs when you close the JSSE socket.
        */
        /* package */
        internal void stopTLS()
        {
            try
            {
                this.stopReaderMessageID = Connection.STOP_READING;
                this.out_Renamed.Close();
                this.in_Renamed.Close();
                //				this.sock.Shutdown(SocketShutdown.Both);
                //				this.sock.Close();
                waitForReader(null);
                this.socket = this.nonTLSBackup;
                this.in_Renamed = (Stream)this.socket.GetStream();
                this.out_Renamed = (Stream)this.socket.GetStream();
                // Allow the new reader to start
                this.stopReaderMessageID = Connection.CONTINUE_READING;
            }
            catch (IOException ioe)
            {
                throw new LdapException(ExceptionMessages.STOPTLS_ERROR, LdapException.CONNECT_ERROR, null, ioe);
            }
            finally
            {
                this.nonTLSBackup = null;
                startReader();
            }
        }
        ///TLS not supported in first release		

        public class ReaderThread
        {
            private void InitBlock(Connection enclosingInstance)
            {
                this.enclosingInstance = enclosingInstance;
            }
            private Connection enclosingInstance;
            public Connection Enclosing_Instance
            {
                get
                {
                    return enclosingInstance;
                }
            }

            public ReaderThread(Connection enclosingInstance)
            {
                InitBlock(enclosingInstance);
            }

            /// <summary> This thread decodes and processes RfcLdapMessage's from the server.
            /// 
            /// Note: This thread needs a graceful shutdown implementation.
            /// </summary>
            public virtual void Run()
            {
                string reason = "reader: thread stopping";
                InterThreadException notify = null;
                Message info = null;
                IOException ioex = null;
                this.enclosingInstance.reader = Thread.CurrentThread;

                try
                {
                    for (; ; )
                    {
                        // -------------------------------------------------------
                        // Decode an RfcLdapMessage directly from the socket.
                        // -------------------------------------------------------
                        Asn1Identifier asn1ID;
                        Stream myIn;
                        /* get current value of in, keep value consistant
                        * though the loop, i.e. even during shutdown
                        */
                        myIn = this.enclosingInstance.in_Renamed;
                        if (myIn == null)
                        {
                            break;
                        }
                        asn1ID = new Asn1Identifier(myIn);
                        int tag = asn1ID.Tag;
                        if (asn1ID.Tag != Asn1Sequence.TAG)
                        {
                            continue; // loop looking for an RfcLdapMessage identifier
                        }

                        // Turn the message into an RfcMessage class
                        Asn1Length asn1Len = new Asn1Length(myIn);

                        RfcLdapMessage msg = new RfcLdapMessage(this.enclosingInstance.decoder, myIn, asn1Len.Length);

                        // ------------------------------------------------------------
                        // Process the decoded RfcLdapMessage.
                        // ------------------------------------------------------------
                        int msgId = msg.MessageID;

                        // Find the message which requested this response.
                        // It is possible to receive a response for a request which
                        // has been abandoned. If abandoned, throw it away
                        try
                        {
                            info = this.enclosingInstance.messages.findMessageById(msgId);
                            info.putReply(msg); // queue & wake up waiting thread
                        }
                        catch (FieldAccessException)
                        {

                            /*
                            * We get the NoSuchFieldException when we could not find
                            * a matching message id.  First check to see if this is
                            * an unsolicited notification (msgID == 0). If it is not
                            * we throw it away. If it is we call any unsolicited
                            * listeners that might have been registered to listen for these
                            * messages.
                            */


                            /* Note the location of this code.  We could have required
                            * that message ID 0 be just like other message ID's but
                            * since message ID 0 has to be treated specially we have
                            * a separate check for message ID 0.  Also note that
                            * this test is after the regular message list has been
                            * checked for.  We could have always checked the list
                            * of messages after checking if this is an unsolicited
                            * notification but that would have inefficient as
                            * message ID 0 is a rare event (as of this time).
                            */
                            if (msgId == 0)
                            {


                                // Notify any listeners that might have been registered
                                this.enclosingInstance.notifyAllUnsolicitedListeners(msg);

                                /*
                                * Was this a server shutdown unsolicited notification.
                                * IF so we quit. Actually calling the return will
                                * first transfer control to the finally clause which
                                * will do the necessary clean up.
                                */
                                if (this.enclosingInstance.unsolSvrShutDnNotification)
                                {
                                    notify = new InterThreadException(ExceptionMessages.SERVER_SHUTDOWN_REQ, new Object[] { this.enclosingInstance.host, this.enclosingInstance.port }, LdapException.CONNECT_ERROR, null, null);

                                    return;
                                }
                            }
                            else
                            {

                            }
                        }
                        if ((this.enclosingInstance.stopReaderMessageID == msgId) || (this.enclosingInstance.stopReaderMessageID == Novell.Directory.Ldap.Connection.STOP_READING))
                        {
                            // Stop the reader Thread.
                            return;
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    // Abort has been called on reader
                    // before closing sockets, from shutdown
                }

                catch (IOException ioe)
                {

                    ioex = ioe;
                    if ((this.enclosingInstance.stopReaderMessageID != Novell.Directory.Ldap.Connection.STOP_READING) && this.enclosingInstance.clientActive)
                    {
                        // Connection lost waiting for results from host:port
                        notify = new InterThreadException(ExceptionMessages.CONNECTION_WAIT, new Object[] { this.enclosingInstance.host, this.enclosingInstance.port }, LdapException.CONNECT_ERROR, ioe, info);
                    }
                    // The connection is no good, don't use it any more
                    this.enclosingInstance.in_Renamed = null;
                    this.enclosingInstance.out_Renamed = null;
                }
                finally
                {
                    /*
                    * There can be four states that the reader can be in at this point:
                    *  1) We are starting TLS and will be restarting the reader
                    *     after we have negotiated TLS.
                    *      - Indicated by whether stopReaderMessageID does not
                    *        equal CONTINUE_READING.
                    *      - Don't call Shutdown.
                    *  2) We are stoping TLS and will be restarting after TLS is
                    *     stopped.
                    *      - Indicated by an IOException AND stopReaderMessageID equals
                    *        STOP_READING - in which case notify will be null.
                    *      - Don't call Shutdown
                    *  3) We receive a Server Shutdown notification.
                    *      - Indicated by messageID equal to 0.
                    *      - call Shutdown.
                    *  4) Another error occured
                    *      - Indicated by an IOException AND notify is not NULL
                    *      - call Shutdown.
                    */
                    if ((!this.enclosingInstance.clientActive) || (notify != null))
                    {
                        //#3 & 4
                        this.enclosingInstance.Dispose(false, reason, 0, notify);
                    }
                    else
                    {
                        this.enclosingInstance.stopReaderMessageID = Connection.CONTINUE_READING;
                    }
                }
                this.enclosingInstance.deadReaderException = ioex;
                this.enclosingInstance.deadReader = this.enclosingInstance.reader;
                this.enclosingInstance.reader = null;
            }
        } // End class ReaderThread

        /// <summary>Add the specific object to the list of listeners that want to be
        /// notified when an unsolicited notification is received.
        /// </summary>
        /* package */
        internal void AddUnsolicitedNotificationListener(LdapUnsolicitedNotificationListener listener)
        {
            unsolicitedListeners.Add(listener);
        }

        /// <summary>Remove the specific object from current list of listeners</summary>
        /* package */
        internal void RemoveUnsolicitedNotificationListener(LdapUnsolicitedNotificationListener listener)
        {
            SupportClass.VectorRemoveElement(unsolicitedListeners, listener);
        }

        /// <summary>Inner class defined so that we can spawn off each unsolicited
        /// listener as a seperate thread.  We did not want to call the
        /// unsolicited listener method directly as this would have tied up our
        /// deamon listener thread in the applications unsolicited listener method.
        /// Since we do not know what the application unsolicited listener
        /// might be doing and how long it will take to process the uncoslicited
        /// notification.  We use this class to spawn off the unsolicited
        /// notification as a separate thread
        /// </summary>
        private class UnsolicitedListenerThread : SupportClass.ThreadClass
        {
            private void InitBlock(Connection enclosingInstance)
            {
                this.enclosingInstance = enclosingInstance;
            }
            private Connection enclosingInstance;
            public Connection Enclosing_Instance
            {
                get
                {
                    return enclosingInstance;
                }
            }
            private LdapUnsolicitedNotificationListener listenerObj;
            private LdapExtendedResponse unsolicitedMsg;

            /* package */
            internal UnsolicitedListenerThread(Connection enclosingInstance, LdapUnsolicitedNotificationListener l, LdapExtendedResponse m)
            {
                InitBlock(enclosingInstance);
                this.listenerObj = l;
                this.unsolicitedMsg = m;
            }

            public override void Run()
            {
                listenerObj.messageReceived(unsolicitedMsg);
            }
        }

        private void notifyAllUnsolicitedListeners(RfcLdapMessage message)
        {
            // MISSING:  If this is a shutdown notification from the server
            // set a flag in the Connection class so that we can throw an
            // appropriate LdapException to the application
            LdapMessage extendedLdapMessage = new LdapExtendedResponse(message);
            string notificationOID = ((LdapExtendedResponse)extendedLdapMessage).ID;
            if (notificationOID.Equals(LdapConnection.SERVER_SHUTDOWN_OID))
            {
                unsolSvrShutDnNotification = true;
            }

            int numOfListeners = unsolicitedListeners.Count;

            // Cycle through all the listeners
            for (int i = 0; i < numOfListeners; i++)
            {
                // Get next listener
                LdapUnsolicitedNotificationListener listener = (LdapUnsolicitedNotificationListener)unsolicitedListeners[i];

                // Create a new ExtendedResponse each time as we do not want each listener
                // to have its own copy of the message
                LdapExtendedResponse tempLdapMessage = new LdapExtendedResponse(message);

                // Spawn a new thread for each listener to go process the message
                // The reason we create a new thread rather than just call the
                // the messageReceived method directly is beacuse we do not know
                // what kind of processing the notification listener class will
                // do.  We do not want our deamon thread to block waiting for
                // the notification listener method to return.
                UnsolicitedListenerThread u = new UnsolicitedListenerThread(this, listener, tempLdapMessage);
                u.Start();
            }
        }
    }
}
