using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace AsyncUdp
{
    public class AsyncSocketEventArgsPool : IDisposable
    {
        int Sockets;
        ConcurrentStack<int> EmptySocketArgs = new();
        SocketAsyncEventArgs[] SocketArgs;

        public class SocketToken
        {
            public int Id;
            public byte[] Buffer;

            public SocketToken(int id, byte[] buffer)
            {
                Id = id; Buffer = buffer; 
            }
        }

        public AsyncSocketEventArgsPool(int Amount, int MaxBufferPer, EventHandler<SocketAsyncEventArgs> eventHandler)
        {
            SocketArgs = new SocketAsyncEventArgs[Amount];
            Sockets = Amount;
            for (int i = 0; i < SocketArgs.Length; i++)
            {
                SocketArgs[i] = new();
                SocketArgs[i].DisconnectReuseSocket = false;
                SocketArgs[i].Completed += eventHandler;
                SocketArgs[i].UserToken = new SocketToken(i,new byte[MaxBufferPer]);
                SocketArgs[i].SetBuffer(((SocketToken)SocketArgs[i].UserToken!).Buffer, 0, MaxBufferPer);
            }
        }

        private int GetFromPool()
        {
            if (!EmptySocketArgs.IsEmpty && EmptySocketArgs.TryPop(out int ID)){
                return ID;
            }
            int Id = Interlocked.Decrement(ref Sockets);
            if(Id < 0)
            {
                Interlocked.Exchange(ref Sockets, 0);
                Id = -1;
            }
            return Id;
        }

        /// <summary>
        /// Returns a reference to the AsyncSocketEventArg allocated for use by the GetFromPool function.
        /// </summary>
        /// <returns>'true' if it could get a SocketAsyncEventArgs from the pool and returns the SocketAsyncEventArgs</returns>
        public bool GetSocketFromPool(out SocketAsyncEventArgs socketAsyncEventArgs)
        {
            int ID = GetFromPool();
            if(ID < 0)
            {
                socketAsyncEventArgs = null!;
                return false;
            }
            socketAsyncEventArgs = SocketArgs[ID];
            return true; 
        }

        /// <summary>
        /// Puts back into the pool
        /// </summary>
        /// <returns>'False' if the value is not within the range of the pool</returns>
        public bool ReturnToPool(in SocketAsyncEventArgs socketAsyncEventArgs)
        {
            //if (socketAsyncEventArgs.UserToken == null || socketAsyncEventArgs.UserToken.GetType() != typeof(SocketToken))
            //    return false;
            EmptySocketArgs.Push(((SocketToken)socketAsyncEventArgs.UserToken!).Id);
            return true;
        }

        public void Dispose()
        {
            EmptySocketArgs.Clear();
            for (int i = 0; i < SocketArgs.Length; i++)
            {
                SocketArgs[i].Dispose();
            }
        }
    }
}
