﻿using Neo.Cryptography;
using Neo.IO;
using Neo.IO.Json;
using Neo.Ledger;
using System.IO;

namespace Neo.Network.P2P.Payloads
{
    public class ConsensusData : ISerializable
    {
        public uint PrimaryIndex;
        public ulong Nonce;

        public UInt256 Hash => new UInt256(Crypto.Default.Hash256(this.ToArray()));
        public int Size => IO.Helper.GetVarSize((int)PrimaryIndex) + sizeof(ulong);

        void ISerializable.Deserialize(BinaryReader reader)
        {
            PrimaryIndex = (uint)reader.ReadVarInt(Blockchain.MaxValidators - 1);
            Nonce = reader.ReadUInt64();
        }

        void ISerializable.Serialize(BinaryWriter writer)
        {
            writer.WriteVarInt(PrimaryIndex);
            writer.Write(Nonce);
        }

        public JObject ToJson()
        {
            JObject json = new JObject();
            json["primary"] = PrimaryIndex;
            json["nonce"] = Nonce.ToString("x16");
            return json;
        }
    }
}
