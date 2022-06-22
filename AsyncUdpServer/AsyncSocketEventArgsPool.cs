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
        byte[] Buffer;

        public AsyncSocketEventArgsPool(int Amount, int MaxBufferPer, EventHandler<SocketAsyncEventArgs> eventHandler)
        {
            SocketArgs = new SocketAsyncEventArgs[Amount];
            Sockets = Amount;
            Buffer = new byte[MaxBufferPer*Amount];
            for (int i = 0; i < SocketArgs.Length; i++)
            {
                SocketArgs[i] = new();
                SocketArgs[i].DisconnectReuseSocket = true;
                SocketArgs[i].SetBuffer(Buffer, i * MaxBufferPer, MaxBufferPer);
                SocketArgs[i].Completed += eventHandler;
                SocketArgs[i].UserToken = i;
            }
        }

        /// <summary>
        /// Thread safe fetching of the next ID in the pool
        /// </summary>
        /// <returns>'true' if an ID is avaliable, If false then the pool is all used up</returns>
        public bool GetFromPool(out int ID)
        {
            if (!EmptySocketArgs.IsEmpty && EmptySocketArgs.TryPop(out int Id)){
                ID = Id;
            }
            ID = Interlocked.Decrement(ref Sockets);
            if(ID < 0)
            {
                Interlocked.Exchange(ref Sockets, 0);
                ID = -1;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Returns a reference to the AsyncSocketEventArg allocated for use by the GetFromPool function.
        /// DO NOT FETCH ONE AT RANDOM WITH THIS FUNCTION, Easiest way to break everything.
        /// </summary>
        /// <returns>'The allocated SocketAsyncEventArgs. IF YOU PUT IN A RANDOM ID, YOU WILL GET AN ERROR</returns>
        public ref SocketAsyncEventArgs GetSocketFromPool(int ID)
        {
            return ref SocketArgs[ID];
        }

        /// <summary>
        /// Once you have finished with a socketEventArg, run this to put it back into the pool
        /// </summary>
        /// <returns>'False' if the value is not within the range of the pool</returns>
        public bool ReturnToPool(int ID)
        {
            if (ID > SocketArgs.Length || ID < 0)
                return false;
            EmptySocketArgs.Push(ID);
            return true;
        }

        /// <summary>
        /// Puts back into the pool
        /// </summary>
        /// <returns>'False' if the value is not within the range of the pool</returns>
        public bool ReturnToPool(SocketAsyncEventArgs socketAsyncEventArgs)
        {
            if (socketAsyncEventArgs.UserToken == null || socketAsyncEventArgs.UserToken.GetType() != typeof(int))
                return false;
            EmptySocketArgs.Push((int)socketAsyncEventArgs.UserToken);
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
