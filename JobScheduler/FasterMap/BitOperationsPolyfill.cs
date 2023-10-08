using System;
using System.Collections.Generic;
using System.Text;

namespace FasterMap
{
    internal class BitOperations
    {
        public static int Log2(uint v)
        {
            return Log2((int)v);
        }
        public static int Log2(int v)
        {
            int r = 0xFFFF - v >> 31 & 0x10;
            v >>= r;
            int shift = 0xFF - v >> 31 & 0x8;
            v >>= shift;
            r |= shift;
            shift = 0xF - v >> 31 & 0x4;
            v >>= shift;
            r |= shift;
            shift = 0x3 - v >> 31 & 0x2;
            v >>= shift;
            r |= shift;
            r |= (v >> 1);
            return r;
        }

        internal static bool IsPow2(uint x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }

        internal static uint RotateRight(uint hashcode, int v)
        {
            throw new NotImplementedException();
        }

        internal static uint RoundUpToPowerOf2(uint length)
        {
            throw new NotImplementedException();
        }
    }
}
