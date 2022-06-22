using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncUdp
{
    public class AsyncUdpServer : IDisposable
    {

        string Address;

        int Port => IPEndpoint.Port;

        int MaxClients;

        int MaxBufferSize;

        IPEndPoint IPEndpoint;
        EndPoint Endpoint;

        AsyncSocketEventArgsPool SendAsyncSocketEventArgsPool;
        AsyncSocketEventArgsPool ReceiveAsyncSocketEventArgsPool;

        SemaphoreQueue SendSemaphoreQueue;
        SemaphoreQueue ReceiveSemaphoreQueue;


        public bool IsStarted { get; private set; }

        Socket _Socket;

        EndPoint _receiveEndpoint;

        public AsyncUdpServer(IPEndPoint endpoint) : this(endpoint, 4){}

        public AsyncUdpServer(IPEndPoint endpoint, int maxClients) : this(endpoint, maxClients, 8192){}

        public AsyncUdpServer(IPEndPoint endpoint, int maxClients, int maxBufferSize)
        {
            Address = endpoint.Address.ToString();
            Endpoint = endpoint;
            IPEndpoint = endpoint;
            MaxClients = maxClients;
            MaxBufferSize = maxBufferSize;
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

            SendSemaphoreQueue = new(MaxClients);
            ReceiveSemaphoreQueue = new(MaxClients);

            SendAsyncSocketEventArgsPool = new AsyncSocketEventArgsPool(MaxClients, MaxBufferSize, OnAsyncCompleted!);
            ReceiveAsyncSocketEventArgsPool = new AsyncSocketEventArgsPool(MaxClients, MaxBufferSize, OnAsyncCompleted!);

            // Create a new server socket
            _Socket = CreateSocket();

            // Update the server socket disposed flag
            IsSocketDisposed = false;

            // Apply the option: dual mode (this option must be applied before recieving)
            if (_Socket.AddressFamily == AddressFamily.InterNetworkV6)
                _Socket.DualMode = true;

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

            try
            {
                // Close the server socket
                _Socket.Close();

                // Dispose the server socket
                _Socket.Dispose();

                // Dispose event arguments
                SendAsyncSocketEventArgsPool.Dispose();
                ReceiveAsyncSocketEventArgsPool.Dispose();

                // Update the server socket disposed flag
                IsSocketDisposed = true;
            }
            catch (ObjectDisposedException) { }

            // Update the started flag
            IsStarted = false;

            // Call the server stopped handler
            OnStopped();

            return true;
        }



        /// <summary>
        /// Receive datagram from the client (asynchronous)
        /// </summary>
        public virtual void StartReceiveAsync()
        {
            // Try to receive datagram
            TryReceive();
        }

        /// <summary>
        /// Try to receive new data
        /// </summary>
        private async void TryReceive()
        {
            if (!IsStarted)
                return;
            await ReceiveSemaphoreQueue.WaitAsync();
            if (!ReceiveAsyncSocketEventArgsPool.GetFromPool(out int ID))
                return;
            var ReceiveSocket = ReceiveAsyncSocketEventArgsPool.GetSocketFromPool(ID);
            try
            {
                // Async receive with the receive handler
                ReceiveSocket.RemoteEndPoint = _receiveEndpoint;
                //_receiveEventArg.SetBuffer(_receiveBuffer, 0, _receiveBuffer.Length);
                
                if (!_Socket.ReceiveFromAsync(ReceiveSocket))
                    ProcessReceiveFrom(ReceiveSocket);
            }
            catch (ObjectDisposedException) { ReceiveAsyncSocketEventArgsPool.ReturnToPool(ID); ReceiveSemaphoreQueue.Release(); }
        }



        /// <summary>
        /// Send datagram to the given endpoint (asynchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to send</param>
        /// <param name="buffer">Datagram buffer to send</param>
        public void Send(EndPoint endpoint, ReadOnlySpan<byte> buffer)
        {
            _ = SendAsync(endpoint, buffer.ToArray(), CancellationToken.None);
        }
        /// <summary>
        /// Send datagram to the given endpoint (asynchronous)
        /// </summary>
        /// <param name="endpoint">Endpoint to send</param>
        /// <param name="buffer">Datagram buffer to send</param>
        /// <param name="cancellationToken">Can be canceled if the queue to send is taking too long</param>
        /// <returns>'true' if the datagram was successfully sent, 'false' if the datagram was not sent</returns>
        public virtual async Task<bool> SendAsync(EndPoint endpoint, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!IsStarted)
                return false;

            if (buffer.Length == 0)
                return true;
            await SendSemaphoreQueue.WaitAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                SendSemaphoreQueue.Release();
                return false;
            }
            if (!SendAsyncSocketEventArgsPool.GetFromPool(out int ID))
                return false;

            var SendSocket = SendAsyncSocketEventArgsPool.GetSocketFromPool(ID);
            buffer.CopyTo(SendSocket.MemoryBuffer.Slice(SendSocket.Offset, buffer.Length));
            EndPoint SendPoint = endpoint;
            SendSocket.RemoteEndPoint = SendPoint;

            try
            {
                // Async write with the write handler
                //_sendEventArg.RemoteEndPoint = _sendEndpoint;
                //_sendEventArg.SetBuffer(_sendBuffer, 0, _sendBuffer.Length);
                if (!_Socket.SendToAsync(SendSocket))
                    ProcessSendTo(SendSocket);
            }
            catch (ObjectDisposedException) { SendAsyncSocketEventArgsPool.ReturnToPool(ID); SendSemaphoreQueue.Release(); }

            return true;
        }



        /// <summary>
        /// This method is called whenever a receive or send operation is completed on a socket
        /// </summary>
        private void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (IsSocketDisposed)
                return;

            // Determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.ReceiveFrom:
                    ProcessReceiveFrom(e);
                    break;
                case SocketAsyncOperation.SendTo:
                    ProcessSendTo(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }

        }

        /// <summary>
        /// This method is invoked when an asynchronous receive from operation completes
        /// </summary>
        private void ProcessReceiveFrom(SocketAsyncEventArgs e)
        {

            TryReceive();
            if (!IsStarted)
            {
                ReceiveAsyncSocketEventArgsPool.ReturnToPool(e);
                ReceiveSemaphoreQueue.Release();
                return;
            }
            EndPoint REndPoint = e.RemoteEndPoint!;
            // Check for error
            if (e.SocketError != SocketError.Success || e.BytesTransferred == 0)
            {
                SendError(e.SocketError);

                // Call the datagram received zero handler
                OnReceived(REndPoint, ReadOnlySpan<byte>.Empty);
                ReceiveAsyncSocketEventArgsPool.ReturnToPool(e);
                ReceiveSemaphoreQueue.Release();
                return;
            }

            // Received some data from the client
            int size = e.BytesTransferred;

            // Call the datagram received handler
            byte[] Received = new byte[size];
            Buffer.BlockCopy(e.Buffer!, e.Offset, Received, 0, size);
            OnReceived(REndPoint, Received);
            ReceiveAsyncSocketEventArgsPool.ReturnToPool(e);
            ReceiveSemaphoreQueue.Release();
        }

        /// <summary>
        /// This method is invoked when an asynchronous send to operation completes
        /// </summary>
        private void ProcessSendTo(SocketAsyncEventArgs e)
        {
            if (!IsStarted)
            {
                SendAsyncSocketEventArgsPool.ReturnToPool(e);
                SendSemaphoreQueue.Release();
                return;
            }
            EndPoint REndPoint = e.RemoteEndPoint!;
            // Check for error
            if (e.SocketError != SocketError.Success)
            {
                SendError(e.SocketError);

                // Call the buffer sent zero handler
                OnSent(REndPoint, 0);
                SendAsyncSocketEventArgsPool.ReturnToPool(e);
                SendSemaphoreQueue.Release();
                return;
            }

            long sent = e.BytesTransferred;
            // Send some data to the client
            if (sent > 0)
            {
                // Call the buffer sent handler
                OnSent(REndPoint, sent);
            }
            SendAsyncSocketEventArgsPool.ReturnToPool(e);
            SendSemaphoreQueue.Release();
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
        protected virtual void OnReceived(EndPoint endpoint, ReadOnlySpan<byte> buffer) { }
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

        /// <summary>
        /// Handle error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        protected virtual void OnError(SocketError error) { }

        #endregion

        #region Error handling

        /// <summary>
        /// Send error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        private void SendError(SocketError error)
        {
            // Skip disconnect errors
            if ((error == SocketError.ConnectionAborted) ||
                (error == SocketError.ConnectionRefused) ||
                (error == SocketError.ConnectionReset) ||
                (error == SocketError.OperationAborted) ||
                (error == SocketError.Shutdown))
                return;

            OnError(error);
        }

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
