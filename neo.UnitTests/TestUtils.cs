﻿using Neo.IO;
using Neo.Network.P2P.Payloads;
using Neo.VM;
using System;
using System.IO;

namespace Neo.UnitTests
{
    public static class TestUtils
    {
        public static readonly Random TestRandom = new Random(1337); // use fixed seed for guaranteed determinism

        public static byte[] GetByteArray(int length, byte firstByte)
        {
            byte[] array = new byte[length];
            array[0] = firstByte;
            for (int i = 1; i < length; i++)
            {
                array[i] = 0x20;
            }
            return array;
        }

        public static Transaction GetTransaction()
        {
            return new Transaction
            {
                Script = new byte[1],
                Sender = UInt160.Zero,
                Attributes = new TransactionAttribute[0],
                Witnesses = new Witness[0]
            };
        }

        public static void SetupHeaderWithValues(Header header, UInt256 val256, out UInt256 merkRootVal, out UInt160 val160, out uint timestampVal, out uint indexVal, out Witness scriptVal)
        {
            setupBlockBaseWithValues(header, val256, out merkRootVal, out val160, out timestampVal, out indexVal, out scriptVal);
        }

        public static void SetupBlockWithValues(Block block, UInt256 val256, out UInt256 merkRootVal, out UInt160 val160, out uint timestampVal, out uint indexVal, out Witness scriptVal, out Transaction[] transactionsVal, int numberOfTransactions)
        {
            setupBlockBaseWithValues(block, val256, out merkRootVal, out val160, out timestampVal, out indexVal, out scriptVal);

            transactionsVal = new Transaction[numberOfTransactions];
            if (numberOfTransactions > 0)
            {
                for (int i = 0; i < numberOfTransactions; i++)
                {
                    transactionsVal[i] = TestUtils.GetTransaction();
                }
            }

            block.ConsensusData = new ConsensusData();
            block.Transactions = transactionsVal;
        }

        private static void setupBlockBaseWithValues(BlockBase bb, UInt256 val256, out UInt256 merkRootVal, out UInt160 val160, out uint timestampVal, out uint indexVal, out Witness scriptVal)
        {
            bb.PrevHash = val256;
            merkRootVal = new UInt256(new byte[] { 49, 73, 102, 67, 23, 43, 100, 236, 22, 37, 65, 124, 112, 39, 36, 66, 127, 219, 57, 69, 11, 184, 182, 127, 132, 95, 64, 200, 252, 206, 222, 197 });
            bb.MerkleRoot = merkRootVal;
            timestampVal = new DateTime(1968, 06, 01, 0, 0, 0, DateTimeKind.Utc).ToTimestamp();
            bb.Timestamp = timestampVal;
            indexVal = 0;
            bb.Index = indexVal;
            val160 = UInt160.Zero;
            bb.NextConsensus = val160;
            scriptVal = new Witness
            {
                InvocationScript = new byte[0],
                VerificationScript = new[] { (byte)OpCode.PUSHT }
            };
            bb.Witness = scriptVal;
        }

        public static Transaction CreateRandomHashTransaction()
        {
            var randomBytes = new byte[16];
            TestRandom.NextBytes(randomBytes);
            return new Transaction
            {
                Script = randomBytes,
                Sender = UInt160.Zero,
                Attributes = new TransactionAttribute[0],
                Witnesses = new Witness[0]
            };
        }

        public static T CopyMsgBySerialization<T>(T serializableObj, T newObj) where T : ISerializable
        {
            using (MemoryStream ms = new MemoryStream(serializableObj.ToArray(), false))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                newObj.Deserialize(reader);
            }

            return newObj;
        }
    }
}
