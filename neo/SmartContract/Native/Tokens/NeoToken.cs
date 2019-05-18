using Neo.Cryptography.ECC;
using Neo.IO;
using Neo.Ledger;
using Neo.Persistence;
using Neo.VM;
using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VMArray = Neo.VM.Types.Array;

namespace Neo.SmartContract.Native.Tokens
{
    public sealed class NeoToken : Nep5Token<NeoToken.AccountState>
    {
        public override string ServiceName => "Neo.Native.Tokens.NEO";
        public override string Name => "NEO";
        public override string Symbol => "neo";
        public override byte Decimals => 0;
        public BigInteger TotalAmount { get; }

        private const byte Prefix_Validator = 33;
        private const byte Prefix_ValidatorsCount = 15;
        private const byte Prefix_NextValidators = 14;

        internal NeoToken()
        {
            this.TotalAmount = 100000000 * Factor;
        }

        protected override StackItem Main(ApplicationEngine engine, string operation, VMArray args)
        {
            switch (operation)
            {
                case "unclaimedGas":
                    return UnclaimedGas(engine.Snapshot, new UInt160(args[0].GetByteArray()), (uint)args[1].GetBigInteger());
                case "registerValidator":
                    return RegisterValidator(engine, args[0].GetByteArray());
                case "vote":
                    return Vote(engine, new UInt160(args[0].GetByteArray()), ((VMArray)args[1]).Select(p => p.GetByteArray().AsSerializable<ECPoint>()).ToArray());
                case "getRegisteredValidators":
                    return GetRegisteredValidators(engine.Snapshot).Select(p => new Struct(new StackItem[] { p.PublicKey.ToArray(), p.Votes })).ToArray();
                case "getValidators":
                    return GetValidators(engine.Snapshot).Select(p => (StackItem)p.ToArray()).ToArray();
                case "getNextBlockValidators":
                    return GetNextBlockValidators(engine.Snapshot).Select(p => (StackItem)p.ToArray()).ToArray();
                default:
                    return base.Main(engine, operation, args);
            }
        }

        public override BigInteger TotalSupply(Snapshot snapshot)
        {
            return TotalAmount;
        }

        protected override void OnBalanceChanging(ApplicationEngine engine, UInt160 account, AccountState state, BigInteger amount)
        {
            DistributeGas(engine, account, state);
            if (amount.IsZero) return;
            if (state.Votes.Length == 0) return;
            foreach (ECPoint pubkey in state.Votes)
            {
                StorageItem storage_validator = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_Validator, pubkey.ToArray()));
                ValidatorState state_validator = ValidatorState.FromByteArray(storage_validator.Value);
                state_validator.Votes += amount;
                storage_validator.Value = state_validator.ToByteArray();
            }
            StorageItem storage_count = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_ValidatorsCount));
            ValidatorsCountState state_count = ValidatorsCountState.FromByteArray(storage_count.Value);
            state_count.Votes[state.Votes.Length - 1] += amount;
            storage_count.Value = state_count.ToByteArray();
        }

        private void DistributeGas(ApplicationEngine engine, UInt160 account, AccountState state)
        {
            BigInteger gas = CalculateBonus(engine.Snapshot, state.Balance, state.BalanceHeight, engine.Snapshot.PersistingBlock.Index);
            state.BalanceHeight = engine.Snapshot.PersistingBlock.Index;
            GAS.Mint(engine, account, gas);
            engine.Snapshot.Storages.GetAndChange(CreateAccountKey(account)).Value = state.ToByteArray();
        }

        private BigInteger CalculateBonus(Snapshot snapshot, BigInteger value, uint start, uint end)
        {
            if (value.IsZero || start >= end) return BigInteger.Zero;
            if (value.Sign < 0) throw new ArgumentOutOfRangeException(nameof(value));
            BigInteger amount = BigInteger.Zero;
            uint ustart = start / Blockchain.DecrementInterval;
            if (ustart < Blockchain.GenerationAmount.Length)
            {
                uint istart = start % Blockchain.DecrementInterval;
                uint uend = end / Blockchain.DecrementInterval;
                uint iend = end % Blockchain.DecrementInterval;
                if (uend >= Blockchain.GenerationAmount.Length)
                {
                    uend = (uint)Blockchain.GenerationAmount.Length;
                    iend = 0;
                }
                if (iend == 0)
                {
                    uend--;
                    iend = Blockchain.DecrementInterval;
                }
                while (ustart < uend)
                {
                    amount += (Blockchain.DecrementInterval - istart) * Blockchain.GenerationAmount[ustart];
                    ustart++;
                    istart = 0;
                }
                amount += (iend - istart) * Blockchain.GenerationAmount[ustart];
            }
            amount += GAS.GetSysFeeAmount(snapshot, end - 1) - (start == 0 ? 0 : GAS.GetSysFeeAmount(snapshot, start - 1));
            return value * amount * GAS.Factor / TotalAmount;
        }

        internal override bool Initialize(ApplicationEngine engine)
        {
            if (!base.Initialize(engine)) return false;
            if (base.TotalSupply(engine.Snapshot) != BigInteger.Zero) return false;
            UInt160 account = Contract.CreateMultiSigRedeemScript(Blockchain.StandbyValidators.Length / 2 + 1, Blockchain.StandbyValidators).ToScriptHash();
            Mint(engine, account, TotalAmount);
            foreach (ECPoint pubkey in Blockchain.StandbyValidators)
                RegisterValidator(engine, pubkey.EncodePoint(true));
            return true;
        }

        protected override bool OnPersist(ApplicationEngine engine)
        {
            if (!base.OnPersist(engine)) return false;
            StorageItem storage = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_NextValidators), () => new StorageItem());
            storage.Value = GetValidators(engine.Snapshot).ToByteArray();
            return true;
        }

        public BigInteger UnclaimedGas(Snapshot snapshot, UInt160 account, uint end)
        {
            StorageItem storage = snapshot.Storages.TryGet(CreateAccountKey(account));
            if (storage is null) return BigInteger.Zero;
            AccountState state = new AccountState(storage.Value);
            return CalculateBonus(snapshot, state.Balance, state.BalanceHeight, end);
        }

        private bool RegisterValidator(ApplicationEngine engine, byte[] pubkey)
        {
            if (pubkey.Length != 33 || (pubkey[0] != 0x02 && pubkey[0] != 0x03))
                throw new ArgumentException();
            StorageKey key = CreateStorageKey(Prefix_Validator, pubkey);
            if (engine.Snapshot.Storages.TryGet(key) != null) return false;
            engine.Snapshot.Storages.Add(key, new StorageItem
            {
                Value = new ValidatorState().ToByteArray()
            });
            return true;
        }

        private bool Vote(ApplicationEngine engine, UInt160 account, ECPoint[] pubkeys)
        {
            if (!InteropService.CheckWitness(engine, account)) return false;
            StorageKey key_account = CreateAccountKey(account);
            if (engine.Snapshot.Storages.TryGet(key_account) is null) return false;
            StorageItem storage_account = engine.Snapshot.Storages.GetAndChange(key_account);
            AccountState state_account = new AccountState(storage_account.Value);
            foreach (ECPoint pubkey in state_account.Votes)
            {
                StorageItem storage_validator = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_Validator, pubkey.ToArray()));
                ValidatorState state_validator = ValidatorState.FromByteArray(storage_validator.Value);
                state_validator.Votes -= state_account.Balance;
                storage_validator.Value = state_validator.ToByteArray();
            }
            pubkeys = pubkeys.Distinct().Where(p => engine.Snapshot.Storages.TryGet(CreateStorageKey(Prefix_Validator, p.ToArray())) != null).ToArray();
            if (pubkeys.Length != state_account.Votes.Length)
            {
                StorageItem storage_count = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_ValidatorsCount), () => new StorageItem
                {
                    Value = new ValidatorsCountState().ToByteArray()
                });
                ValidatorsCountState state_count = ValidatorsCountState.FromByteArray(storage_count.Value);
                if (state_account.Votes.Length > 0)
                    state_count.Votes[state_account.Votes.Length - 1] -= state_account.Balance;
                if (pubkeys.Length > 0)
                    state_count.Votes[pubkeys.Length - 1] += state_account.Balance;
                storage_count.Value = state_count.ToByteArray();
            }
            state_account.Votes = pubkeys;
            storage_account.Value = state_account.ToByteArray();
            foreach (ECPoint pubkey in state_account.Votes)
            {
                StorageItem storage_validator = engine.Snapshot.Storages.GetAndChange(CreateStorageKey(Prefix_Validator, pubkey.ToArray()));
                ValidatorState state_validator = ValidatorState.FromByteArray(storage_validator.Value);
                state_validator.Votes += state_account.Balance;
                storage_validator.Value = state_validator.ToByteArray();
            }
            return true;
        }

        public IEnumerable<(ECPoint PublicKey, BigInteger Votes)> GetRegisteredValidators(Snapshot snapshot)
        {
            return snapshot.Storages.Find(new[] { Prefix_Validator }).Select(p =>
            (
                p.Key.Key.Skip(1).ToArray().AsSerializable<ECPoint>(),
                ValidatorState.FromByteArray(p.Value.Value).Votes
            ));
        }

        public ECPoint[] GetValidators(Snapshot snapshot)
        {
            StorageItem storage_count = snapshot.Storages.TryGet(CreateStorageKey(Prefix_ValidatorsCount));
            if (storage_count is null) return Blockchain.StandbyValidators;
            ValidatorsCountState state_count = ValidatorsCountState.FromByteArray(storage_count.Value);
            int count = (int)state_count.Votes.Select((p, i) => new
            {
                Count = i,
                Votes = p
            }).Where(p => p.Votes.Sign > 0).ToArray().WeightedFilter(0.25, 0.75, p => p.Votes, (p, w) => new
            {
                p.Count,
                Weight = w
            }).WeightedAverage(p => p.Count, p => p.Weight);
            count = Math.Max(count, Blockchain.StandbyValidators.Length);
            HashSet<ECPoint> sv = new HashSet<ECPoint>(Blockchain.StandbyValidators);
            return GetRegisteredValidators(snapshot).Where(p => (p.Votes.Sign > 0) || sv.Contains(p.PublicKey)).OrderByDescending(p => p.Votes).ThenBy(p => p.PublicKey).Select(p => p.PublicKey).Take(count).OrderBy(p => p).ToArray();
        }

        public ECPoint[] GetNextBlockValidators(Snapshot snapshot)
        {
            StorageItem storage = snapshot.Storages.TryGet(CreateStorageKey(Prefix_NextValidators));
            if (storage is null) return Blockchain.StandbyValidators;
            return storage.Value.AsSerializableArray<ECPoint>();
        }

        public class AccountState : Nep5AccountState
        {
            public uint BalanceHeight;
            public ECPoint[] Votes;

            public AccountState()
            {
                this.Votes = new ECPoint[0];
            }

            public AccountState(byte[] data)
                : base(data)
            {
            }

            protected override void FromStruct(Struct @struct)
            {
                base.FromStruct(@struct);
                BalanceHeight = (uint)@struct[1].GetBigInteger();
                Votes = @struct[2].GetByteArray().AsSerializableArray<ECPoint>(Blockchain.MaxValidators);
            }

            protected override Struct ToStruct()
            {
                Struct @struct = base.ToStruct();
                @struct.Add(BalanceHeight);
                @struct.Add(Votes.ToByteArray());
                return @struct;
            }
        }

        internal class ValidatorState
        {
            public BigInteger Votes;

            public static ValidatorState FromByteArray(byte[] data)
            {
                return new ValidatorState
                {
                    Votes = new BigInteger(data)
                };
            }

            public byte[] ToByteArray()
            {
                return Votes.ToByteArray();
            }
        }

        internal class ValidatorsCountState
        {
            public BigInteger[] Votes = new BigInteger[Blockchain.MaxValidators];

            public static ValidatorsCountState FromByteArray(byte[] data)
            {
                using (MemoryStream ms = new MemoryStream(data, false))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    BigInteger[] votes = new BigInteger[(int)r.ReadVarInt(Blockchain.MaxValidators)];
                    for (int i = 0; i < votes.Length; i++)
                        votes[i] = new BigInteger(r.ReadVarBytes());
                    return new ValidatorsCountState
                    {
                        Votes = votes
                    };
                }
            }

            public byte[] ToByteArray()
            {
                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.WriteVarInt(Votes.Length);
                    foreach (BigInteger vote in Votes)
                        w.WriteVarBytes(vote.ToByteArray());
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }
    }
}
