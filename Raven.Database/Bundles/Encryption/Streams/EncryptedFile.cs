using System;
using System.Runtime.InteropServices;

namespace Raven.Bundles.Encryption.Streams
{
    /// <summary>
    /// Support class for things common to the EncryptedInputStream and EncryptedOutputStream.
    /// 
    /// Logical positions are positions as reported to the stream reader/writer.
    /// Physical positions are the positions on disk.
    /// </summary>
    internal static class EncryptedFile
    {
        public const ulong DefaultMagicNumber = 0x2064657470797243; // "Crypted "
        public const ulong WithTotalSizeMagicNumber = 0x3175768581897343; // "Crypted with additional fields"

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Header
        {
            public ulong MagicNumber;
            public int IVSize;
            public int DecryptedBlockSize;
            public int EncryptedBlockSize;
            public long TotalUnencryptedSize;

            public static readonly int HeaderSize = Marshal.SizeOf(typeof(Header));

            public int ActualHeaderSize
            {
                get
                {
                    int headerSize;
                    switch (MagicNumber)
                    {
                        case DefaultMagicNumber: //old header struct --> without the last field
                            headerSize = HeaderSize - Marshal.SizeOf(typeof(long));
                            break;
                        case WithTotalSizeMagicNumber:
                            headerSize = HeaderSize;
                            break;
                        default:
                            throw new ApplicationException("Invalid magic number");
                    }
                    return headerSize;
                }
            }

            public int DiskBlockSize
            {
                get { return EncryptedBlockSize + IVSize; }
            }

            public long GetBlockNumberFromPhysicalPosition(long position)
            {
                return (position - ActualHeaderSize) / DiskBlockSize;
            }

            public long GetBlockNumberFromLogicalPosition(long position)
            {
                return position / DecryptedBlockSize;
            }

            public long GetPhysicalPositionFromBlockNumber(long number)
            {
                return DiskBlockSize * number + ActualHeaderSize;
            }

            public long GetLogicalPositionFromBlockNumber(long number)
            {
                return DecryptedBlockSize * number;
            }

            public long GetBlockOffsetFromLogicalPosition(long position)
            {
                var blockNumber = GetBlockNumberFromLogicalPosition(position);
                var blockStart = GetLogicalPositionFromBlockNumber(blockNumber);
                return position - blockStart;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct Footer
        {
            public long TotalLength;

            public static readonly int FooterSize = Marshal.SizeOf(typeof(Footer));
        }

        public class Block
        {
            public long BlockNumber;
            public long TotalEncryptedStreamLength;
            public byte[] Data;
        }
    }
}
