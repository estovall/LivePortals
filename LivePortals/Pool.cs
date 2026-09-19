using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections.LowLevel.Unsafe;

namespace LivePortals
{
    /// <summary>
    /// Arrays of the capture pipeline, kept and handed out again by exact length. A capture at 768 px allocated
    /// close to a gigabyte of arrays that each lived a second (the pixels read back from the GPU, the sky mask,
    /// the depth, the layers), and every one of the collector's passes over that stopped the game's main thread:
    /// the stutter for a few seconds after each trip on a machine with cores to spare. Any thread. The pool as a
    /// whole holds no more than PoolBudget.MaxBytes; beyond that, returned arrays go to the collector.
    /// </summary>
    internal static class Pool<T> where T : struct
    {
        private static readonly Dictionary<int, Stack<T[]>> _free = new Dictionary<int, Stack<T[]>>();
        private static readonly int _size = UnsafeUtility.SizeOf<T>();

        /// <summary>An array of exactly this length, zeroed.</summary>
        internal static T[] Rent(int length)
        {
            var a = Take(length);
            if (a == null) return new T[length];
            Array.Clear(a, 0, a.Length);
            return a;
        }

        /// <summary>An array of exactly this length whose contents are whatever they were; for callers that write every element.</summary>
        internal static T[] RentDirty(int length) => Take(length) ?? new T[length];

        /// <summary>Hand an array back. Null is fine. Never return an array anything still reads.</summary>
        internal static void Return(T[] a)
        {
            if (a == null || a.Length == 0) return;
            long bytes = (long)a.Length * _size;
            if (!PoolBudget.TryAdd(bytes)) return;
            lock (_free)
            {
                if (!_free.TryGetValue(a.Length, out var st)) _free[a.Length] = st = new Stack<T[]>();
                st.Push(a);
            }
        }

        private static T[] Take(int length)
        {
            T[] a = null;
            lock (_free)
                if (_free.TryGetValue(length, out var st) && st.Count > 0) a = st.Pop();
            if (a != null) PoolBudget.Remove((long)a.Length * _size);
            return a;
        }
    }

    internal static class PoolBudget
    {
        internal const long MaxBytes = 160L << 20;
        private static long _held;
        internal static long Held => Interlocked.Read(ref _held);

        internal static bool TryAdd(long bytes)
        {
            while (true)
            {
                long h = Interlocked.Read(ref _held);
                if (h + bytes > MaxBytes) return false;
                if (Interlocked.CompareExchange(ref _held, h + bytes, h) == h) return true;
            }
        }

        internal static void Remove(long bytes) => Interlocked.Add(ref _held, -bytes);
    }
}
