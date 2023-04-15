using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace AsyncUdp
{
    public class ReuseableBufferPool
    {
        byte[] _Buffer;
        readonly ConcurrentQueue<int> ReleasedBuffers;

        private readonly int SizePerBuffer;
        private int BuffersUsed;
        private int BufferAmount = 0;
        private readonly int MaxAmountOfBuffers;

        public ReuseableBufferPool(int sizePerBuffer, int BufferCount)
        {
            SizePerBuffer = sizePerBuffer;
            BuffersUsed = -sizePerBuffer;
            MaxAmountOfBuffers = BufferCount;
            _Buffer = GC.AllocateArray<byte>(SizePerBuffer * BufferCount, pinned: true);
            ReleasedBuffers = new();
        }

        public bool GetBuffer([MaybeNullWhen(false)] out Memory<byte> BufferSlice, [MaybeNullWhen(false)] out int BufferOffset)
        {
            if (ReleasedBuffers.TryDequeue(out int ID))
            {
                BufferOffset = ID;
                BufferSlice = _Buffer.AsMemory(BufferOffset, SizePerBuffer);
                return true;
            }
            if (BufferAmount == MaxAmountOfBuffers)
            {
                BufferSlice = null;
                BufferOffset = 0;
                return false;
            }
            Interlocked.Increment(ref BufferAmount);
            BuffersUsed = Interlocked.Add(ref BuffersUsed, SizePerBuffer);
            BufferOffset = BuffersUsed;
            BufferSlice = _Buffer.AsMemory(BuffersUsed, SizePerBuffer);
            return true;
        }

        public void ReturnBuffer(in int BufferOffset)
        {
            ReleasedBuffers.Enqueue(BufferOffset);
        }
    }
}