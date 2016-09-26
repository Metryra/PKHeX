﻿using System;
using System.Linq;
using System.Security.Cryptography;

namespace PKHeX
{
    public sealed class SAV3Colosseum : SaveFile
    {
        public override string BAKName => $"{FileName} [{OT} ({Version}) - {PlayTimeString}].bak";
        public override string Filter => "GameCube Save File|*.gci";
        public override string Extension => ".gci";

        // 3 Save files are stored
        // 0x0000-0x6000 contains memory card data
        // 0x6000-0x60000 contains the 3 save slots
        // 0x5A000 / 3 = 0x1E000 per save slot
        // Checksum is SHA1 over 0-0x1DFD7, stored in the last 20 bytes of the save slot.
        // Another SHA1 hash is 0x1DFD8, for 20 bytes. Unknown purpose.
        // Checksum is used as the crypto key.

        private const int SLOT_SIZE = 0x1E000;
        private const int SLOT_START = 0x6000;
        private const int SLOT_COUNT = 3;

        private readonly int SaveCount = -1;
        private readonly int SaveIndex = -1;
        private readonly ushort[] LegalItems, LegalKeyItems, LegalBalls, LegalTMHMs, LegalBerries, LegalCologne;
        private readonly int OFS_PouchCologne;
        public SAV3Colosseum(byte[] data = null)
        {
            Data = data == null ? new byte[SaveUtil.SIZE_G3BOX] : (byte[])data.Clone();
            BAK = (byte[])Data.Clone();
            Exportable = !Data.SequenceEqual(new byte[Data.Length]);

            if (SaveUtil.getIsG3CXDSAV(Data) != GameVersion.COLO)
                return;

            OriginalData = (byte[])Data.Clone();

            // Scan all 3 save slots for the highest counter
            for (int i = 0; i < SLOT_COUNT; i++)
            {
                int slotOffset = SLOT_START + i * SLOT_SIZE;
                int SaveCounter = BigEndian.ToInt32(Data, slotOffset + 4);
                if (SaveCounter <= SaveCount)
                    continue;

                SaveCount = SaveCounter;
                SaveIndex = i;
            }

            // Decrypt most recent save slot
            {
                byte[] slot = new byte[SLOT_SIZE];
                int slotOffset = SLOT_START + SaveIndex * SLOT_SIZE;
                Array.Copy(Data, slotOffset, slot, 0, slot.Length);
                byte[] digest = new byte[20];
                Array.Copy(slot, SLOT_SIZE - 20, digest, 0, digest.Length);

                // Decrypt Slot
                Data = DecryptColosseum(slot, digest);
            }

            Trainer1 = 0x00078;
            Party = 0x000A8;
            OFS_PouchHeldItem = 0x007F8;
            OFS_PouchKeyItem = 0x00848;
            OFS_PouchBalls = 0x0087C;
            OFS_PouchTMHM = 0x00934;
            OFS_PouchBerry = 0x009BC;
            OFS_PouchCologne = 0x00AEC; // Cologne

            Box = 0x00B90;
            Daycare = 0x08170;

            LegalItems = Legal.Pouch_Items_COLO;
            LegalKeyItems = Legal.Pouch_Key_COLO;
            LegalBalls = Legal.Pouch_Ball_RS;
            LegalTMHMs = Legal.Pouch_TM_RS; // not HMs
            LegalBerries = Legal.Pouch_Berries_RS;
            LegalCologne = Legal.Pouch_Cologne_CXD;

            Personal = PersonalTable.RS;
            HeldItems = Legal.HeldItems_COLO;

            if (!Exportable)
                resetBoxes();
        }

        private readonly byte[] OriginalData;
        public override byte[] Write(bool DSV)
        {
            setChecksums();

            // Get updated save slot data
            byte[] digest = Data.Skip(Data.Length - 20).Take(20).ToArray();
            byte[] newSAV = EncryptColosseum(Data, digest);

            // Put save slot back in original save data
            byte[] newFile = (byte[])OriginalData.Clone();
            Array.Copy(newSAV, 0, newFile, SLOT_START + SaveIndex*SLOT_SIZE, newSAV.Length);
            return newFile; 
        }

        // Configuration
        public override SaveFile Clone() { return new SAV3Colosseum(Write(DSV: false)); }

        public override int SIZE_STORED => PKX.SIZE_3CSTORED;
        public override int SIZE_PARTY => PKX.SIZE_3CSTORED; // unused
        public override PKM BlankPKM => new CK3();
        public override Type PKMType => typeof(CK3);

        public override int MaxMoveID => 354;
        public override int MaxSpeciesID => 386;
        public override int MaxAbilityID => 77;
        public override int MaxItemID => 374;
        public override int MaxBallID => 0xC;
        public override int MaxGameID => 5;
        
        public override int MaxEV => 255;
        public override int Generation => 3;
        protected override int GiftCountMax => 1;
        public override int OTLength => 8;
        public override int NickLength => 10;
        public override int MaxMoney => 999999;

        public override int BoxCount => 3;

        // Checksums
        private readonly SHA1 sha1 = SHA1.Create();
        private byte[] EncryptColosseum(byte[] input, byte[] digest)
        {
            if (input.Length != SLOT_SIZE)
                return null;

            byte[] d = (byte[])input.Clone();
            byte[] k = (byte[])digest.Clone(); // digest

            // NOT key
            for (int i = 0; i < 20; i++)
                k[i] = (byte)~k[i];

            for (int i = 0x18; i < 0x1DFD8; i += 20)
            {
                for (int j = 0; j < 20; j++)
                    d[i + j] ^= k[j];
                k = sha1.ComputeHash(d, i, 20); // update digest
            }
            return d;
        }
        private byte[] DecryptColosseum(byte[] input, byte[] digest)
        {
            if (input.Length != SLOT_SIZE)
                return null;

            byte[] d = (byte[])input.Clone();
            byte[] k = (byte[])digest.Clone();

            // NOT key
            for (int i = 0; i < 20; i++)
                k[i] = (byte)~k[i];

            for (int i = 0x18; i < 0x1DFD8; i += 20)
            {
                byte[] key = sha1.ComputeHash(d, i, 20); // update digest
                for (int j = 0; j < 20; j++)
                    d[i + j] ^= k[j];
                Array.Copy(key, k, 20); // for use in next loop
            }
            return d;
        }
        protected override void setChecksums()
        {
            // Clear Header Checksum
            BitConverter.GetBytes(0).CopyTo(Data, 12);
            // Compute checksum of data
            byte[] checksum = sha1.ComputeHash(Data, 0, 0x1DFD8);
            // Set Checksum to end
            Array.Copy(checksum, 0, Data, Data.Length - 20, 20);

            // Header Integrity
            byte[] H = new byte[8]; Array.Copy(checksum, 0, H, 0, 8);
            byte[] D = new byte[8]; Array.Copy(Data, 0x18, D, 0, 8);
            // Decrypt Checksum
            for (int i = 0; i < 8; i++)
                D[i] ^= (byte)~H[i];

            // Compute new header checksum
            int newHC = 0;
            for (int i = 0; i < 0x18; i += 4)
                newHC -= BigEndian.ToInt32(Data, i);

            newHC -= BigEndian.ToInt32(D, 0);
            newHC -= BigEndian.ToInt32(D, 4);

            // Set Header Checksum
            BigEndian.GetBytes(newHC).CopyTo(Data, 12);
        }
        public override bool ChecksumsValid => !ChecksumInfo.Contains("Invalid");
        public override string ChecksumInfo
        {
            get
            {
                byte[] data = (byte[])Data.Clone();
                int oldHC = BigEndian.ToInt32(data, 12);
                // Clear Header Checksum
                BitConverter.GetBytes(0).CopyTo(data, 12);
                byte[] checksum = sha1.ComputeHash(data, 0, 0x1DFD8);
                // Header Integrity
                byte[] H = new byte[8];
                byte[] D = new byte[8];

                Array.Copy(checksum, 0, H, 0, H.Length);
                Array.Copy(data, 0x00018, D, 0, D.Length);
                for (int i = 0; i < 8; i++)
                    D[i] ^= (byte)~H[i];

                // Compute new header checksum
                int newHC = 0;
                for (int i = 0; i < 0x18; i += 4)
                    newHC -= BigEndian.ToInt32(data, i);

                newHC -= BigEndian.ToInt32(D, 0);
                newHC -= BigEndian.ToInt32(D, 4);

                byte[] chk = data.Skip(data.Length - 20).Take(20).ToArray();

                bool header = newHC == oldHC;
                bool body = chk.SequenceEqual(checksum);
                return $"Header Checksum {(header ? "V" : "Inv")}alid, Body Checksum {(body ? "V" : "Inv")}alid.";
            }
        }

        // Trainer Info
        public override GameVersion Version { get { return GameVersion.COLO; } protected set { } }

        // Storage
        public override int getPartyOffset(int slot)
        {
            return Party + SIZE_STORED * slot;
        }
        public override int getBoxOffset(int box)
        {
            return Box + (30 * SIZE_STORED + 0x14)*box + 0x14;
        }
        public override int CurrentBox { get { return 0; } set { } }
        public override int getBoxWallpaper(int box)
        {
            return box;
        }
        public override string getBoxName(int box)
        {
            return PKX.getColoStr(Data, Box + 0x24A4*box, 8);
        }
        public override void setBoxName(int box, string value)
        {
            if (value.Length > 8)
                value = value.Substring(0, 8); // Hard cap
            PKX.setColoStr(value, 8).CopyTo(Data, Box + 0x24A4*box);
        }
        public override PKM getPKM(byte[] data)
        {
            return new CK3(data.Take(SIZE_STORED).ToArray());
        }
        public override byte[] decryptPKM(byte[] data)
        {
            return data;
        }

        protected override void setDex(PKM pkm) { }
        
        private TimeSpan PlayedSpan
        {
            get { return TimeSpan.FromSeconds((double)(Util.SwapEndianness(BitConverter.ToUInt32(Data, 40)) - 0x47000000) / 128); }
            set { BitConverter.GetBytes(Util.SwapEndianness((uint)(value.TotalSeconds * 128) + 0x47000000)).CopyTo(Data, 40); }
        }
        public override int PlayedHours
        {
            get { return (ushort)PlayedSpan.Hours; }
            set { var time = PlayedSpan; PlayedSpan = time - TimeSpan.FromHours(time.Hours) + TimeSpan.FromHours(value); }
        }
        public override int PlayedMinutes
        {
            get { return (byte)PlayedSpan.Minutes; }
            set { var time = PlayedSpan; PlayedSpan = time - TimeSpan.FromMinutes(time.Minutes) + TimeSpan.FromMinutes(value); }
        }
        public override int PlayedSeconds
        {
            get { return (byte)PlayedSpan.Seconds; }
            set { var time = PlayedSpan; PlayedSpan = time - TimeSpan.FromSeconds(time.Seconds) + TimeSpan.FromSeconds(value); }
        }

        // Trainer Info (offset 0x78, length 0xB18, end @ 0xB90)
        public override string OT { get { return PKX.getColoStr(Data, 0x78, 10); } set { PKX.setColoStr(value, 10).CopyTo(Data, 0x78); } }
        private string OT2 { get { return PKX.getColoStr(Data, 0x8C, 10); } set { PKX.setColoStr(value, 10).CopyTo(Data, 0x8C); OT = value; } }
        public override ushort SID { get { return BigEndian.ToUInt16(Data, 0xA4); } set { BigEndian.GetBytes(value).CopyTo(Data, 0xA4); } }
        public override ushort TID { get { return BigEndian.ToUInt16(Data, 0xA6); } set { BigEndian.GetBytes(value).CopyTo(Data, 0xA6); } }

        public override int Gender { get { return Data[0xAF8]; } set { Data[0xAF8] = (byte)value; } }
        public override uint Money { get { return BigEndian.ToUInt32(Data, 0xAFC); } set { BigEndian.GetBytes(value).CopyTo(Data, 0xAFC); } }
        public uint Coupons { get { return BigEndian.ToUInt32(Data, 0xB00); } set { BigEndian.GetBytes(value).CopyTo(Data, 0xB00); } }
        public string RUI_Name { get { return PKX.getColoStr(Data, 0xB3A, 10); } set { PKX.setColoStr(value, 10).CopyTo(Data, 0xB3A); } }

        public override InventoryPouch[] Inventory
        {
            get
            {
                InventoryPouch[] pouch =
                {
                    new InventoryPouch(InventoryType.Items, LegalItems, 995, OFS_PouchHeldItem, 20), // 20 COLO, 30 XD
                    new InventoryPouch(InventoryType.KeyItems, LegalKeyItems, 1, OFS_PouchKeyItem, 43),
                    new InventoryPouch(InventoryType.TMHMs, LegalBalls, 995, OFS_PouchTMHM, 16),
                    new InventoryPouch(InventoryType.TMHMs, LegalTMHMs, 995, OFS_PouchTMHM, 64),
                    new InventoryPouch(InventoryType.Berries, LegalBerries, 995, OFS_PouchBerry, 46),
                    new InventoryPouch(InventoryType.Medicine, LegalCologne, 995, OFS_PouchCologne, 3), // Cologne
                };
                foreach (var p in pouch)
                    p.getPouchBigEndian(ref Data);
                return pouch;
            }
            set
            {
                foreach (var p in value)
                    p.setPouchBigEndian(ref Data);
            }
        }


        // Daycare Structure:
        // 0x00 -- Occupied
        // 0x01 -- Deposited Level
        // 0x02-0x03 -- unused?
        // 0x04-0x07 -- Initial EXP
        public override int getDaycareSlotOffset(int loc, int slot) { return Daycare + 8; }
        public override ulong? getDaycareRNGSeed(int loc) { return null; }
        public override uint? getDaycareEXP(int loc, int slot) { return null; }
        public override bool? getDaycareOccupied(int loc, int slot) { return null; }
        public override void setDaycareEXP(int loc, int slot, uint EXP) { }
        public override void setDaycareOccupied(int loc, int slot, bool occupied) { }
    }
    internal static class BigEndian
    {
        internal static uint ToUInt32(byte[] data, int offset)
        {
            int val = 0;
            val |= data[offset + 0] << 24;
            val |= data[offset + 1] << 16;
            val |= data[offset + 2] << 8;
            val |= data[offset + 3] << 0;
            return (uint)val;
        }
        internal static ushort ToUInt16(byte[] data, int offset)
        {
            int val = 0;
            val |= data[offset + 0] << 8;
            val |= data[offset + 1] << 0;
            return (ushort)val;
        }
        internal static int ToInt32(byte[] data, int offset)
        {
            int val = 0;
            val |= data[offset + 0] << 24;
            val |= data[offset + 1] << 16;
            val |= data[offset + 2] << 8;
            val |= data[offset + 3] << 0;
            return val;
        }
        internal static short ToInt16(byte[] data, int offset)
        {
            int val = 0;
            val |= data[offset + 0] << 8;
            val |= data[offset + 1] << 0;
            return (short)val;
        }
        internal static byte[] GetBytes(int value)
        {
            return Invert(BitConverter.GetBytes(value));
        }
        internal static byte[] GetBytes(short value)
        {
            return Invert(BitConverter.GetBytes(value));
        }
        internal static byte[] GetBytes(uint value)
        {
            return Invert(BitConverter.GetBytes(value));
        }
        internal static byte[] GetBytes(ushort value)
        {
            return Invert(BitConverter.GetBytes(value));
        }
        private static byte[] Invert(byte[] data)
        {
            byte[] result = new byte[data.Length];
            int o = 0;
            int i = data.Length;
            while (o != data.Length)
                result[--i] = data[o++];
            return result;
        }
    }
}
