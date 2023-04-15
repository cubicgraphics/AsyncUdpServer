using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncUdp
{
    public class AsyncUdpServer : IDisposable
    {
        public string Address { get; private set; }
        public int Port => IPEndpoint.Port;
        private readonly bool ReceiveAsync;
        public IPEndPoint IPEndpoint { get; private set; }
        public EndPoint Endpoint { get; private set; }

        private byte[] SerialBuffer;
        ReuseableBufferPool _BufferPool;
        public bool IsStarted { get; private set; }
        Socket _Socket;
        EndPoint _receiveEndpoint;


        /// <summary>
        /// Async Udp server
        /// </summary>
        /// <remarks>
        /// If receiveAsync is set to true, then the server will start recieving the next packet while the current one is still being handled
        /// </remarks>
        public AsyncUdpServer(IPEndPoint endpoint, bool receiveAsync)
        {
            Address = endpoint.Address.ToString();
            Endpoint = endpoint;
            IPEndpoint = endpoint;
            ReceiveAsync = receiveAsync;
        }

        /// <summary>
        /// Create a new socket object
        /// </summary>
        /// <remarks>
        /// Method may be override if you need to prepare some specific socket object in your implementation.
        /// </remarks>
        /// <returns>Socket object</returns>
        protected virtual Socket CreateSocket()
        {
            return new Socket(Endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        }

        /// <summary>
        /// Start the server (synchronous)
        /// </summary>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start</returns>
        public virtual bool Start()
        {
            if (IsStarted)
                return false;
            // Setup event args

            // Create a new server socket
            _Socket = CreateSocket();

            // Update the server socket disposed flag
            IsSocketDisposed = false;

            // Apply the option: dual mode (this option must be applied before recieving)
            if (_Socket.AddressFamily == AddressFamily.InterNetworkV6)
                _Socket.DualMode = true;


            _BufferPool = new ReuseableBufferPool(8192);

            // Bind the server socket to the endpoint
            _Socket.Bind(Endpoint);
            // Refresh the endpoint property based on the actual endpoint created
            Endpoint = _Socket.LocalEndPoint!;

            // Call the server starting handler
            OnStarting();

            // Prepare receive endpoint
            _receiveEndpoint = new IPEndPoint((Endpoint.AddressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Any : IPAddress.Any, 0);

            // Update the started flag
            IsStarted = true;

            StartReceive();
            // Call the server started handler
            OnStarted();

            return true;
        }

        /// <summary>
        /// Stop the server (synchronous)
        /// </summary>
        /// <returns>'true' if the server was successfully stopped, 'false' if the server is already stopped</returns>
        public virtual bool Stop()
        {
            if (!IsStarted)
                return false;

            // Call the server stopping handler
            OnStopping();

            // Update the started flag
            IsStarted = false;
            try
            {
                // Close the server socket
                _Socket.Close();

                // Dispose the server socket
                _Socket.Dispose();

                // Update the server socket disposed flag
                IsSocketDisposed = true;
            }
            catch (ObjectDisposedException) { }



            // Call the server stopped handler
            OnStopped();

            return true;
        }



        private void StartReceive()
        {
            // Try to receive datagram
            if (ReceiveAsync)
            {
                RecieveAsync();
                return;
            }
            SerialBuffer = GC.AllocateArray<byte>(65527, pinned: true);
            RecieveSerial();
        }

        private async void RecieveAsync()
        {
            if(!IsStarted)
                return;
            _BufferPool.GetBuffer(out Memory<byte> BufferSlice, out int BufferId);
            EndPoint RecvEndpoint = _receiveEndpoint;
            SocketReceiveFromResult recvResult;
            try
            {
                recvResult = await _Socket.ReceiveFromAsync(BufferSlice, SocketFlags.None, RecvEndpoint);
            }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => RecieveAsync());

            var recvPacket = BufferSlice[..recvResult.ReceivedBytes];
            OnReceived(recvResult.RemoteEndPoint, recvPacket);
            _BufferPool.ReturnBuffer(BufferId);

        }

        private async void RecieveSerial()
        {
            while (IsStarted)
            {
                Memory<byte> RecvBuffer = SerialBuffer.AsMemory();
                EndPoint RecvEndpoint = _receiveEndpoint;
                SocketReceiveFromResult recvResult;
                try
                {
                    recvResult = await _Socket.ReceiveFromAsync(RecvBuffer, SocketFlags.None, RecvEndpoint);
                }
                catch (SocketException) { RecieveSerial(); return; }
                catch (ObjectDisposedException) { RecieveSerial(); return; }

                var recvPacket = RecvBuffer[..recvResult.ReceivedBytes];
                OnReceived(recvResult.RemoteEndPoint, recvPacket);
            }
        }

        public async virtual void SendAsync(EndPoint endpoint, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                await _Socket.SendToAsync(buffer, SocketFlags.None, endpoint, cancellationToken);
            }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
        }

        /// <summary>
        /// Send datagram to the given endpoint (asynchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to send</param>
        /// <param name="buffer">Datagram buffer to send</param>
        public void SendAsync(EndPoint endpoint, Memory<byte> buffer)
        {
            SendAsync(endpoint, buffer, CancellationToken.None);
        }

        #region Datagram handlers / Override-able methords

        /// <summary>
        /// Handle server starting notification
        /// </summary>
        protected virtual void OnStarting() { }
        /// <summary>
        /// Handle server started notification
        /// </summary>
        protected virtual void OnStarted() { }
        /// <summary>
        /// Handle server stopping notification
        /// </summary>
        protected virtual void OnStopping() { }
        /// <summary>
        /// Handle server stopped notification
        /// </summary>
        protected virtual void OnStopped() { }

        /// <summary>
        /// Handle datagram received notification
        /// </summary>
        /// <param name="endpoint">Received endpoint</param>
        /// <param name="buffer">Received datagram buffer</param>
        /// <param name="offset">Received datagram buffer offset</param>
        /// <param name="size">Received datagram buffer size</param>
        /// <remarks>
        /// Notification is called when another datagram was received from some endpoint
        /// </remarks>
        protected virtual void OnReceived(EndPoint endpoint, Memory<byte> buffer) { }
        /// <summary>
        /// Handle datagram sent notification
        /// </summary>
        /// <param name="endpoint">Endpoint of sent datagram</param>
        /// <param name="sent">Size of sent datagram buffer</param>
        /// <remarks>
        /// Notification is called when a datagram was sent to the client.
        /// This handler could be used to send another datagram to the client for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(EndPoint endpoint, long sent) { }

        #endregion

        #region IDisposable implementation

        /// <summary>
        /// Disposed flag
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Server socket disposed flag
        /// </summary>
        public bool IsSocketDisposed { get; private set; } = true;

        // Implement IDisposable.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposingManagedResources)
        {
            // The idea here is that Dispose(Boolean) knows whether it is
            // being called to do explicit cleanup (the Boolean is true)
            // versus being called due to a garbage collection (the Boolean
            // is false). This distinction is useful because, when being
            // disposed explicitly, the Dispose(Boolean) method can safely
            // execute code using reference type fields that refer to other
            // objects knowing for sure that these other objects have not been
            // finalized or disposed of yet. When the Boolean is false,
            // the Dispose(Boolean) method should not execute code that
            // refer to reference type fields because those objects may
            // have already been finalized."

            if (!IsDisposed)
            {
                if (disposingManagedResources)
                {
                    // Dispose managed resources here...
                    Stop();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                IsDisposed = true;
            }
        }

        #endregion
    }
}