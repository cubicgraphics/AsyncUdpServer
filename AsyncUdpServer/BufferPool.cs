using System;
using System.Collections.Concurrent;
using System.Threading;

namespace AsyncUdp
{
    public class ReuseableBufferPool
    {
        ConcurrentDictionary<int,byte[]> Buffers;
        ConcurrentQueue<int> ReleasedBuffers;

        private readonly int SizePerBuffer;
        private int CurrentBufferPos = -1;

        public ReuseableBufferPool(int sizePerBuffer)
        {
            Buffers = new();
            ReleasedBuffers = new();
            SizePerBuffer = sizePerBuffer;
        }

        public void GetBuffer(out Memory<byte> BufferSlice, out int BufferId)
        {
            if(ReleasedBuffers.TryDequeue(out int ID))
            {
                BufferId = ID;
                if (Buffers.TryGetValue(ID, out byte[] Buffer))
                {
                    BufferSlice = Buffer.AsMemory();
                    return;
                }
            }
            BufferId = Interlocked.Increment(ref CurrentBufferPos);
            Buffers.TryAdd(BufferId, GC.AllocateArray<byte>(SizePerBuffer, pinned: true));
            BufferSlice = Buffers[BufferId].AsMemory();
        }

        public void ReturnBuffer(in int BufferId)
        {
            ReleasedBuffers.Enqueue(BufferId);
        }

        public int GetCurrentBufferCount()
        {
            return CurrentBufferPos + 1;
        }

    }
}
