﻿/* This program is free software. It comes without any warranty, to
 * the extent permitted by applicable law. You can redistribute it
 * and/or modify it under the terms of the Do What The Fuck You Want
 * To Public License, Version 2, as published by Sam Hocevar. See
 * http://www.wtfpl.net/ for more details. */

using System;
using System.IO;
using System.Net;
using System.Xml;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Runtime.Serialization.Json;

namespace CFW2OFW
{
    static class G
    {
#pragma warning disable S2223
        static public readonly Queue<KeyValuePair<string, string>> patchURLs = new Queue<KeyValuePair<string, string>>();
        static public readonly Queue<string> patchFNames = new Queue<string>();
        static public readonly XmlDocument xmlDoc = new XmlDocument();
        static public string currentDir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
        static public readonly string makeNpdata = currentDir + "\\make_npdata.exe";
        static public readonly string patchPath = currentDir + "\\patch";
        static public readonly WebClient wc = new WebClient();
        static public string gameName = "";
        static public string newID = "";
        static public string ID = "";
        static public string newVer = "";
        static public int verOffset;
        static public int catOffset;
        static public string outputDir = "";
        static public string sourceDir = "";
        static public string contentID = "";
        static public uint size = 0;
        static public bool NoCheck = true;
        static public bool CopyOnly = false;
        static public bool Pause = true;
        static public bool GenericCID = false;
        static public int hasEm = 0;
#pragma warning restore S2223
        static public void Exit(string msg)
        {
            G.Exit(msg, 1);
        }
        static public void Exit(string msg, int code)
        {
            Console.WriteLine(msg);
            Console.Write("Press any key to exit . . .");
            Console.ReadKey(true);
            Console.Write(" Exiting");
            Environment.Exit(code);
        }
    }

    static public class PS3
    {
        private readonly static byte[] AesKey = {
            0x2E, 0x7B, 0x71, 0xD7, 0xC9, 0xC9, 0xA1, 0x4E,
            0xA3, 0x22, 0x1F, 0x18, 0x88, 0x28, 0xB8, 0xF8
        };

        private static byte[] PKGFileKey;

        private static uint uiEncryptedFileStartOffset;

        private static byte[] DecryptedHeader = new byte[1024 * 1024];

        internal static bool IncrementArray(ref byte[] sourceArray, int position)
        {
            if (sourceArray[position] == 0xFF)
            {
                if (position != 0)
                {
                    if (IncrementArray(ref sourceArray, position - 1))
                    {
                        sourceArray[position] = 0x00;
                        return true;
                    }
                    else return false;
                }
                else return false;
            }
            else
            {
                sourceArray[position] += 0x01;
                return true;
            }
        }

        internal static class PkgExtract
        {
            internal static string HexStringToAscii(string HexString)
            {
                try
                {
                    var StrValue = new StringBuilder();
                    while (HexString.Length > 0)
                    {
                        StrValue.Append(Convert.ToChar(Convert.ToUInt32(HexString.Substring(0, 2), 16)).ToString());
                        HexString = HexString.Substring(2, HexString.Length - 2);
                    }
                    return StrValue.ToString().Replace("\0", "");
                }
                catch (Exception)
                {
                    return null;
                }
            }

            internal static string ByteArrayToAscii(byte[] ByteArray, int startPos, int length)
            {
                byte[] byteArrayPhrase = new byte[length];
                Array.Copy(ByteArray, startPos, byteArrayPhrase, 0, byteArrayPhrase.Length);
                string hexPhrase = ByteArrayToHexString(byteArrayPhrase);
                return HexStringToAscii(hexPhrase);
            }

            internal static string ByteArrayToHexString(byte[] ByteArray)
            {
                var HexString = new StringBuilder();
                for (int i = 0; i < ByteArray.Length; ++i)
                    HexString.Append(ByteArray[i].ToString("X2"));
                return HexString.ToString();
            }

            internal static byte[] DecryptData(int dataSize, long dataRelativeOffset, Stream encrPKGReadStream, BinaryReader brEncrPKG)
            {
                int size = dataSize % 16;
                size = size > 0 ? ((dataSize / 16) + 1) * 16 : dataSize;
                var PKGFileKeyConsec = new byte[size];
                var incPKGFileKey = new byte[PKGFileKey.Length];
                Array.Copy(PKGFileKey, incPKGFileKey, PKGFileKey.Length);
                encrPKGReadStream.Seek(dataRelativeOffset + uiEncryptedFileStartOffset, SeekOrigin.Begin);
                var EncryptedData = brEncrPKG.ReadBytes(size);
                for (long pos = 0; pos < dataRelativeOffset; pos += 16)
                    IncrementArray(ref incPKGFileKey, PKGFileKey.Length - 1);

                for (long pos = 0; pos < size; pos += 16)
                {
                    Array.Copy(incPKGFileKey, 0, PKGFileKeyConsec, pos, PKGFileKey.Length);
                    IncrementArray(ref incPKGFileKey, PKGFileKey.Length - 1);
                }
                byte[] PKGXorKeyConsec = AesEngine.Encrypt(PKGFileKeyConsec, AesKey, AesKey, CipherMode.ECB, PaddingMode.None);
                return XorEngine.XOR(EncryptedData, 0, PKGXorKeyConsec.Length, PKGXorKeyConsec);
            }

            public static void ExtractFiles(string encryptedPKGFileName)
            {
                int twentyMb = 1024 * 1024 * 20;
                UInt64 ExtractedFileOffset = 0, ExtractedFileSize = 0;
                uint OffsetShift = 0;
                long positionIdx = 0;

                string WorkDir = $@"{G.outputDir}\{G.ID}\";

                if (!Directory.Exists(WorkDir))
                    Directory.CreateDirectory(WorkDir);

                byte[] FileTable = new byte[320000], dumpFile, firstFileOffset = new byte[4],
                    firstNameOffset = new byte[4], Offset = new byte[8], Size = new byte[8],
                    NameOffset = new byte[4], NameSize = new byte[4], Name;
                byte contentType = 0, fileType = 0;
                var isFile = false;

                var encrPKGReadStream = new FileStream(encryptedPKGFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var brEncrPKG = new BinaryReader(encrPKGReadStream);

                Array.Copy(DecryptedHeader, 0, FileTable, 0, FileTable.Length);

                Array.Copy(FileTable, 0, firstNameOffset, 0, firstNameOffset.Length);
                Array.Reverse(firstNameOffset);
                uint uifirstNameOffset = BitConverter.ToUInt32(firstNameOffset, 0);

                uint uiFileNr = uifirstNameOffset / 32;
                
                if ((int)uiFileNr < 0)
                    G.Exit("An error occured during the extraction operation, because of a decryption error");

                Array.Copy(FileTable, 12, firstFileOffset, 0, firstFileOffset.Length);
                Array.Reverse(firstFileOffset);
                int uifirstFileOffset = (int)BitConverter.ToUInt32(firstFileOffset, 0);

                FileTable = new byte[uifirstFileOffset];
                Array.Copy(DecryptedHeader, 0, FileTable, 0, uifirstFileOffset);
                
                for (int ii = 0; ii < (int)uiFileNr; ++ii)
                {
                    Array.Copy(FileTable, positionIdx + 8, Offset, 0, Offset.Length);
                    Array.Reverse(Offset);
                    ExtractedFileOffset = BitConverter.ToUInt64(Offset, 0) + OffsetShift;

                    Array.Copy(FileTable, positionIdx + 16, Size, 0, Size.Length);
                    Array.Reverse(Size);
                    ExtractedFileSize = BitConverter.ToUInt64(Size, 0);

                    Array.Copy(FileTable, positionIdx, NameOffset, 0, NameOffset.Length);
                    Array.Reverse(NameOffset);
                    uint ExtractedFileNameOffset = BitConverter.ToUInt32(NameOffset, 0);

                    Array.Copy(FileTable, positionIdx + 4, NameSize, 0, NameSize.Length);
                    Array.Reverse(NameSize);
                    uint ExtractedFileNameSize = BitConverter.ToUInt32(NameSize, 0);

                    contentType = FileTable[positionIdx + 24];
                    fileType = FileTable[positionIdx + 27];

                    Name = new byte[ExtractedFileNameSize];
                    Array.Copy(FileTable, (int)ExtractedFileNameOffset, Name, 0, ExtractedFileNameSize);

                    FileStream ExtractedFileWriteStream = null;
                    
                    if ((fileType == 0x04) && (ExtractedFileSize == 0x00))
                        isFile = false;
                    else
                        isFile = true;
                    
                    byte[] DecryptedData = DecryptData((int)ExtractedFileNameSize, ExtractedFileNameOffset, encrPKGReadStream, brEncrPKG);
                    Array.Copy(DecryptedData, 0, Name, 0, ExtractedFileNameSize);
                    string ExtractedFileName = ByteArrayToAscii(Name, 0, Name.Length);

                    if (!isFile)
                    {
                        if (!Directory.Exists(ExtractedFileName))
                            Directory.CreateDirectory(WorkDir + ExtractedFileName);
                    }
                    else
                    {
                        if (File.Exists(WorkDir + ExtractedFileName))
                            File.Delete(WorkDir + ExtractedFileName);
                        ExtractedFileWriteStream = new FileStream(WorkDir + ExtractedFileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                    }

                    if (contentType != 0x90 && isFile)
                    {
                        var ExtractedFile = new BinaryWriter(ExtractedFileWriteStream);

                        double division = (double)ExtractedFileSize / twentyMb;
                        UInt64 pieces = (UInt64)Math.Floor(division);
                        UInt64 mod = ExtractedFileSize % (UInt64)twentyMb;
                        if (mod > 0)
                            pieces += 1;

                        dumpFile = new byte[twentyMb];
                        long elapsed = 0;
                        for (UInt64 i = 0; i < pieces; i++)
                        {
                            if ((mod > 0) && (i == (pieces - 1)))
                                dumpFile = new byte[mod];

                            byte[] Decrypted_Data = DecryptData(dumpFile.Length, (long)ExtractedFileOffset + elapsed, encrPKGReadStream, brEncrPKG);
                            elapsed += dumpFile.Length;
                            
                            ExtractedFile.Write(Decrypted_Data, 0, dumpFile.Length);
                        }
                        ExtractedFile.Close();
                    }

                    positionIdx = positionIdx + 32;
                }
                brEncrPKG.Close();

            }
        }

        static public class PkgDecrypt
        {
            static public void DecryptPKGFile(string PKGFileName)
            {
                byte[] EncryptedFileStartOffset = new byte[8];

                var PKGReadStream = new FileStream(PKGFileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var brPKG = new BinaryReader(PKGReadStream);
                
                PKGReadStream.Seek(0x07, SeekOrigin.Begin);
                var pkgType = brPKG.ReadByte();

                if (pkgType != 0x01)
                    G.Exit("This is not a PS3 PKG.");

                PKGReadStream.Seek(0x14, SeekOrigin.Begin);
                var FileChunks = new byte[4];
                FileChunks = brPKG.ReadBytes(FileChunks.Length);
                Array.Reverse(FileChunks);
                var uiFileChunks = BitConverter.ToUInt32(FileChunks, 0);
                
                PKGReadStream.Seek(0x20, SeekOrigin.Begin);
                EncryptedFileStartOffset = brPKG.ReadBytes(EncryptedFileStartOffset.Length);
                Array.Reverse(EncryptedFileStartOffset);
                uiEncryptedFileStartOffset = BitConverter.ToUInt32(EncryptedFileStartOffset, 0);
                
                PKGReadStream.Seek(0x70, SeekOrigin.Begin);
                PKGFileKey = brPKG.ReadBytes(16);
                var incPKGFileKey = new byte[16];
                Array.Copy(PKGFileKey, incPKGFileKey, PKGFileKey.Length);
                
                PKGReadStream.Seek((int)uiEncryptedFileStartOffset, SeekOrigin.Begin);

                byte[] EncryptedDataList = brPKG.ReadBytes((int)(uiFileChunks * 0x20)),
                    PKGFileKeyConsec = new byte[EncryptedDataList.Length], PKGXorKeyConsec;

                for (int pos = 0; pos < EncryptedDataList.Length; pos += AesKey.Length)
                {
                    Array.Copy(incPKGFileKey, 0, PKGFileKeyConsec, pos, PKGFileKey.Length);
                    IncrementArray(ref incPKGFileKey, PKGFileKey.Length - 1);
                }
                PKGXorKeyConsec = AesEngine.Encrypt(PKGFileKeyConsec, AesKey, AesKey, CipherMode.ECB, PaddingMode.None);

                int offset = 0;

                var DecryptedDataList = XorEngine.XOR(EncryptedDataList, 0, PKGXorKeyConsec.Length, PKGXorKeyConsec);

                Array.Copy(DecryptedDataList, 0, DecryptedHeader, 0, DecryptedDataList.Length);

                offset = DecryptedDataList.Length;

                for (uint i = 0; i < uiFileChunks; i++)
                {
                    var size = BitConverter.ToUInt32(DecryptedDataList, (int)(i * 0x20) + 4);
                    size = (size & 0x000000FFU) << 24 | (size & 0x0000FF00U) << 8 | (size & 0x00FF0000U) >> 8 | (size & 0xFF000000U) >> 24;
                    size = (size & 0xFFFFFFF0U) + 0x10;
                    var EncryptedDataEntry = brPKG.ReadBytes((int)size);
                    PKGFileKeyConsec = new byte[EncryptedDataEntry.Length];

                    for (int pos = 0; pos < EncryptedDataEntry.Length; pos += AesKey.Length)
                    {
                        Array.Copy(incPKGFileKey, 0, PKGFileKeyConsec, pos, PKGFileKey.Length);
                        IncrementArray(ref incPKGFileKey, PKGFileKey.Length - 1);
                    }
                    PKGXorKeyConsec = AesEngine.Encrypt(PKGFileKeyConsec, AesKey, AesKey, CipherMode.ECB, PaddingMode.None);

                    var DecryptedDataEntry = XorEngine.XOR(EncryptedDataEntry, 0, PKGXorKeyConsec.Length, PKGXorKeyConsec);

                    Array.Copy(DecryptedDataEntry, 0, DecryptedHeader, offset, DecryptedDataEntry.Length);

                    offset += DecryptedDataEntry.Length;
                }
                PkgExtract.ExtractFiles(PKGFileName);
                for (int ii = 0; ii < 1024 * 1024; ++ii)
                    DecryptedHeader[ii] = 0;
            }
        }

        static protected class AesEngine
        {
            static public byte[] Encrypt(byte[] clearData, byte[] Key, byte[] IV, CipherMode cipherMode, PaddingMode paddingMode)
            {
                var ms = new MemoryStream();
                var alg = Rijndael.Create();
                alg.Mode = cipherMode;
                alg.Padding = paddingMode;
                alg.Key = Key;
                alg.IV = IV;
                var cs = new CryptoStream(ms, alg.CreateEncryptor(), CryptoStreamMode.Write);
                cs.Write(clearData, 0, clearData.Length);
                cs.Close();
                var encryptedData = ms.ToArray();
                return encryptedData;
            }
        }

        static protected class XorEngine
        {
            static public byte[] XOR(byte[] inByteArray, int offsetPos, int length, byte[] XORKey)
            {
                if (inByteArray.Length < offsetPos + length)
                    G.Exit("Combination of chosen offset pos. & Length goes outside of the array to be xored.");
                if ((length % XORKey.Length) != 0)
                    G.Exit("Number of bytes to be XOR'd isn't a mutiple of the XOR key length.");
                int pieces = length / XORKey.Length;
                var outByteArray = new byte[length];
                for (int i = 0; i < pieces; i++)
                for (int pos = 0; pos < XORKey.Length; pos++)
                    outByteArray[(i * XORKey.Length) + pos] += (byte)(inByteArray[offsetPos + (i * XORKey.Length) + pos] ^ XORKey[pos]);
                return outByteArray;
            }
        }
    }

    [DataContract]
    internal class EmJsonStructure
    {
#pragma warning disable 0649
        [DataMember]
        internal string[] titleIds;
        [DataMember]
        internal int works;
        [DataMember]
        internal string note;
#pragma warning restore 0649
    }

    static class Program
    {
        static byte[] Crc32(byte[] data)
        {
            uint[] table = {
                0x00000000, 0x77073096, 0xEE0E612C, 0x990951BA, 0x076DC419, 0x706AF48F,
                0xE963A535, 0x9E6495A3, 0x0EDB8832, 0x79DCB8A4, 0xE0D5E91E, 0x97D2D988,
                0x09B64C2B, 0x7EB17CBD, 0xE7B82D07, 0x90BF1D91, 0x1DB71064, 0x6AB020F2,
                0xF3B97148, 0x84BE41DE, 0x1ADAD47D, 0x6DDDE4EB, 0xF4D4B551, 0x83D385C7,
                0x136C9856, 0x646BA8C0, 0xFD62F97A, 0x8A65C9EC, 0x14015C4F, 0x63066CD9,
                0xFA0F3D63, 0x8D080DF5, 0x3B6E20C8, 0x4C69105E, 0xD56041E4, 0xA2677172,
                0x3C03E4D1, 0x4B04D447, 0xD20D85FD, 0xA50AB56B, 0x35B5A8FA, 0x42B2986C,
                0xDBBBC9D6, 0xACBCF940, 0x32D86CE3, 0x45DF5C75, 0xDCD60DCF, 0xABD13D59,
                0x26D930AC, 0x51DE003A, 0xC8D75180, 0xBFD06116, 0x21B4F4B5, 0x56B3C423,
                0xCFBA9599, 0xB8BDA50F, 0x2802B89E, 0x5F058808, 0xC60CD9B2, 0xB10BE924,
                0x2F6F7C87, 0x58684C11, 0xC1611DAB, 0xB6662D3D, 0x76DC4190, 0x01DB7106,
                0x98D220BC, 0xEFD5102A, 0x71B18589, 0x06B6B51F, 0x9FBFE4A5, 0xE8B8D433,
                0x7807C9A2, 0x0F00F934, 0x9609A88E, 0xE10E9818, 0x7F6A0DBB, 0x086D3D2D,
                0x91646C97, 0xE6635C01, 0x6B6B51F4, 0x1C6C6162, 0x856530D8, 0xF262004E,
                0x6C0695ED, 0x1B01A57B, 0x8208F4C1, 0xF50FC457, 0x65B0D9C6, 0x12B7E950,
                0x8BBEB8EA, 0xFCB9887C, 0x62DD1DDF, 0x15DA2D49, 0x8CD37CF3, 0xFBD44C65,
                0x4DB26158, 0x3AB551CE, 0xA3BC0074, 0xD4BB30E2, 0x4ADFA541, 0x3DD895D7,
                0xA4D1C46D, 0xD3D6F4FB, 0x4369E96A, 0x346ED9FC, 0xAD678846, 0xDA60B8D0,
                0x44042D73, 0x33031DE5, 0xAA0A4C5F, 0xDD0D7CC9, 0x5005713C, 0x270241AA,
                0xBE0B1010, 0xC90C2086, 0x5768B525, 0x206F85B3, 0xB966D409, 0xCE61E49F,
                0x5EDEF90E, 0x29D9C998, 0xB0D09822, 0xC7D7A8B4, 0x59B33D17, 0x2EB40D81,
                0xB7BD5C3B, 0xC0BA6CAD, 0xEDB88320, 0x9ABFB3B6, 0x03B6E20C, 0x74B1D29A,
                0xEAD54739, 0x9DD277AF, 0x04DB2615, 0x73DC1683, 0xE3630B12, 0x94643B84,
                0x0D6D6A3E, 0x7A6A5AA8, 0xE40ECF0B, 0x9309FF9D, 0x0A00AE27, 0x7D079EB1,
                0xF00F9344, 0x8708A3D2, 0x1E01F268, 0x6906C2FE, 0xF762575D, 0x806567CB,
                0x196C3671, 0x6E6B06E7, 0xFED41B76, 0x89D32BE0, 0x10DA7A5A, 0x67DD4ACC,
                0xF9B9DF6F, 0x8EBEEFF9, 0x17B7BE43, 0x60B08ED5, 0xD6D6A3E8, 0xA1D1937E,
                0x38D8C2C4, 0x4FDFF252, 0xD1BB67F1, 0xA6BC5767, 0x3FB506DD, 0x48B2364B,
                0xD80D2BDA, 0xAF0A1B4C, 0x36034AF6, 0x41047A60, 0xDF60EFC3, 0xA867DF55,
                0x316E8EEF, 0x4669BE79, 0xCB61B38C, 0xBC66831A, 0x256FD2A0, 0x5268E236,
                0xCC0C7795, 0xBB0B4703, 0x220216B9, 0x5505262F, 0xC5BA3BBE, 0xB2BD0B28,
                0x2BB45A92, 0x5CB36A04, 0xC2D7FFA7, 0xB5D0CF31, 0x2CD99E8B, 0x5BDEAE1D,
                0x9B64C2B0, 0xEC63F226, 0x756AA39C, 0x026D930A, 0x9C0906A9, 0xEB0E363F,
                0x72076785, 0x05005713, 0x95BF4A82, 0xE2B87A14, 0x7BB12BAE, 0x0CB61B38,
                0x92D28E9B, 0xE5D5BE0D, 0x7CDCEFB7, 0x0BDBDF21, 0x86D3D2D4, 0xF1D4E242,
                0x68DDB3F8, 0x1FDA836E, 0x81BE16CD, 0xF6B9265B, 0x6FB077E1, 0x18B74777,
                0x88085AE6, 0xFF0F6A70, 0x66063BCA, 0x11010B5C, 0x8F659EFF, 0xF862AE69,
                0x616BFFD3, 0x166CCF45, 0xA00AE278, 0xD70DD2EE, 0x4E048354, 0x3903B3C2,
                0xA7672661, 0xD06016F7, 0x4969474D, 0x3E6E77DB, 0xAED16A4A, 0xD9D65ADC,
                0x40DF0B66, 0x37D83BF0, 0xA9BCAE53, 0xDEBB9EC5, 0x47B2CF7F, 0x30B5FFE9,
                0xBDBDF21C, 0xCABAC28A, 0x53B39330, 0x24B4A3A6, 0xBAD03605, 0xCDD70693,
                0x54DE5729, 0x23D967BF, 0xB3667A2E, 0xC4614AB8, 0x5D681B02, 0x2A6F2B94,
                0xB40BBE37, 0xC30C8EA1, 0x5A05DF1B, 0x2D02EF8D
            };
            unchecked
            {
                uint crc = (uint)(((uint)0) ^ (-1));
                var len = data.Length;
                for (var i = 0; i < len; i++)
                    crc = (crc >> 8) ^ table[(crc ^ data[i]) & 0xFF];
                crc = (uint)(crc ^ (-1));
                if (crc < 0)
                    crc += (uint)4294967296;
                var result = BitConverter.GetBytes(crc);
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(result);
                return result;
            }
        }

        static void GenerateLIC(string LICPath, string gameID)
        {
            var data = new Byte[0x900];
            byte[] magic = {
                0x50, 0x53, 0x33, 0x4C, 0x49, 0x43, 0x44, 0x41,
                0x00, 0x00, 0x00, 0x01, 0x80, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01 };
            int i = -1;
            foreach (byte single in magic)
                data[++i] = single;
            i = 0x1F;
            while (i < 0x8FF)
                data[++i] = 0;
            i = 0x800;
            data[i] = 1;
            var characters = gameID.ToCharArray();
            foreach (char single in characters)
                data[++i] = (byte)single;
            var crc = Crc32(data);
            i = 0x1F;
            foreach (byte single in crc)
                data[++i] = single;
            byte[] padding = new Byte[0x10000 - 0x900];
            int l = padding.Length;
            for (i = 0; i < l; ++i)
                padding[i] = 0;
            var LIC = new FileStream(LICPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            var bLIC = new BinaryWriter(LIC);
            bLIC.Write(data);
            bLIC.Write(padding);
            bLIC.Close();
        }

        static void UpdatesFailure()
        {
            Cyan(G.ID);
            Console.Write(" is ");
            Red("not compatible");
        }

        static void Updates()
        {
            try
            {
                G.xmlDoc.LoadXml(G.wc.DownloadString("https://a0.ww.np.dl.playstation.net/tpl/np/" + G.ID + "/" + G.ID + "-ver.xml"));
            }
            catch (WebException e)
            {
                switch(e.Status)
                {
                case WebExceptionStatus.ProtocolError:
                    UpdatesFailure();
                    break;
                default:
                    Console.Write("No internet connection found.");
                    break;
                }
                G.Exit("");
            }
            catch (Exception e)
            {
                UpdatesFailure();
                Console.Write(", but this could be untrue, because there was an\nerror detected while parsing the update entry:\n");
                G.Exit(e.Message);
            }
        }

        static string GetSHA1(string path)
        {
            long size = new FileInfo(path).Length - 0x20;
            if (size < 0x20)
                return "invalid file";
            var formatted = new StringBuilder(40);
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open))
            {
                using (var sha1 = new SHA1Managed())
                {
                    var stream = new BufferedStream(mmf.CreateViewStream(0, size));
                    var hash = sha1.ComputeHash(stream);
                    foreach (byte b in hash)
                        formatted.AppendFormat("{0:x2}", b);
                    stream.Close();
                }
            }
            return formatted.ToString();
        }

        static void GetPatch(KeyValuePair<string, string> entry, string part)
        {
            string url = entry.Key,
                fname = url.Substring(url.LastIndexOf("/", StringComparison.Ordinal) + 1),
                path = $@"{G.patchPath}\{fname}";
            var exists = File.Exists(path);
            Console.Write(fname + " ... ");
            var message = "local";
            if ((exists && GetSHA1(path) != entry.Value) || !exists)
            {
                if (exists) File.Delete(path);
                var wait = new Object();
                lock (wait)
                {
                    G.wc.DownloadFileAsync(new Uri(url), part, wait);
                    System.Threading.Monitor.Wait(wait);
                }
                message = "done";
            }
            if (File.Exists(part)) File.Move(part, path);
            G.patchFNames.Enqueue(fname);
            Green(message);
        }

        static void GetPatches()
        {
            Console.WriteLine($"{G.patchURLs.Count} patches were found for {G.gameName}");
            Console.Write("Size of updates: ");
            Green(G.size.ToString("N0"));
            Console.Write(" bytes\n");
            Console.Write("Depending on your internet speed and the size of updates this might take some\ntime, so ");
            Red("please be patient!\n");
            Console.WriteLine("Downloading or checking SHA1 hash:");
            uint FailedPatches = 0;
            G.wc.DownloadProgressChanged += Wc_DownloadProgressChanged;
            G.wc.DownloadFileCompleted += Wc_DownloadFileCompleted;
            while (G.patchURLs.Count > 0)
            {
                var part = G.patchPath + "\\partial";
                if (File.Exists(part)) File.Delete(part);
                try
                {
                    GetPatch(G.patchURLs.Dequeue(), part);
                }
                catch (Exception)
                {
                    if (File.Exists(part)) File.Delete(part);
                    Red(" failed");
                    ++FailedPatches;
                }
                Console.Write("\n");
            }
            G.wc.DownloadFileCompleted -= Wc_DownloadFileCompleted;
            G.wc.DownloadProgressChanged -= Wc_DownloadProgressChanged;
            if (FailedPatches > 0)
                G.Exit("Not all patches were downloaded, please try again");
        }

        private static void Wc_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            int p = e.ProgressPercentage;
            p = p < 99 ? p : 99;
            Console.Write("{0:00}%\b\b\b", p);
        }

        private static void Wc_DownloadFileCompleted(object state, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            lock (e.UserState)
                System.Threading.Monitor.Pulse(e.UserState);
        }

        static void ProcessPatches()
        {
            string d = " done", f = " failed\n";
            Console.WriteLine("\nExtracting PKGs:");
            if (!Directory.Exists(G.outputDir))
                Directory.CreateDirectory(G.outputDir);
            foreach (string fname in G.patchFNames)
            {
                var path = $"{G.patchPath}\\{fname}";
                Console.Write(fname + " ...");
                try
                {
                    PS3.PkgDecrypt.DecryptPKGFile(path);
                    Green(d);
                }
                catch (Exception ex)
                {
                    Red(f);
                    G.Exit("Error:\n" + ex.Message);
                }
                Console.Write("\n");
            }
        }

        static string ProcessParam(string ParamPath)
        {
            var B = SeekOrigin.Begin;
            var ParamStream = new FileStream(ParamPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bParam = new BinaryReader(ParamStream);
            var paramDict = new Dictionary<string, KeyValuePair<int, int>>();

            ParamStream.Seek(0x00, B);
            byte[] paramMagic = { 0x00, 0x50, 0x53, 0x46, 0x01, 0x01, 0x00, 0x00 };
            if (!((IStructuralEquatable)paramMagic).Equals(bParam.ReadBytes(8), StructuralComparisons.StructuralEqualityComparer))
                G.Exit("Invalid PARAM.SFO");
            var lilEndian = BitConverter.IsLittleEndian;

            ParamStream.Seek(0x08, B);
            var header_0 = bParam.ReadBytes(4);
            if (!lilEndian) Array.Reverse(header_0);
            var keyTableStart = BitConverter.ToUInt32(header_0, 0);

            ParamStream.Seek(0x0C, B);
            var header_1 = bParam.ReadBytes(4);
            if (!lilEndian) Array.Reverse(header_1);
            var dataTableStart = BitConverter.ToUInt32(header_1, 0);

            ParamStream.Seek(0x10, B);
            var header_2 = bParam.ReadBytes(4);
            if (!lilEndian) Array.Reverse(header_2);
            var tablesEntries = BitConverter.ToUInt32(header_2, 0);

            ParamStream.Seek((int)keyTableStart, B);
            var parameter_block_raw = bParam.ReadBytes((int)dataTableStart - (int)keyTableStart);
            var parameter_block_string = new StringBuilder();
            foreach (byte character in parameter_block_raw) parameter_block_string.Append((char)character);
            var Parameters = parameter_block_string.ToString().Split((char)0);
            int offset = 0x14;
            for (int i = 0; i < tablesEntries; ++i)
            {
                ParamStream.Seek(offset, B);
                offset += 0x10;
                var key = bParam.ReadBytes(0x10);
                if (key[2] != 0x04 || key[3] != 0x02) continue;
                byte[] data_len = new byte[4],
                    data_offset_rel = new byte[4];
                Array.Copy(key, 4, data_len, 0, 4);
                Array.Copy(key, 12, data_offset_rel, 0, 4);
                if (!lilEndian)
                {
                    Array.Reverse(data_len);
                    Array.Reverse(data_offset_rel);
                }
                var dataLen = BitConverter.ToUInt32(data_len, 0);
                var dataOffsetRel = BitConverter.ToUInt32(data_offset_rel, 0);
                paramDict.Add(Parameters[i], new KeyValuePair<int, int>((int)dataOffsetRel + (int)dataTableStart, (int)dataLen));
            }
            if (!paramDict.ContainsKey("TITLE") || !paramDict.ContainsKey("APP_VER") || !paramDict.ContainsKey("CATEGORY"))
                G.Exit("Error while parsing PARAM.SFO\nTITLE, APP_VER and CATEGORY entries are missing.");
            var TitleID = paramDict["TITLE_ID"];
            ParamStream.Seek(TitleID.Key, B);
            var ret = new String(bParam.ReadChars(TitleID.Value)).Substring(0, 9);
            G.verOffset = paramDict["APP_VER"].Key;
            G.catOffset = paramDict["CATEGORY"].Key;
            bParam.Close();
            return ret;
        }

        static Boolean MoveTest(string split, Regex[] regexes)
        {
            if (regexes[0].IsMatch(split) ||
                regexes[1].IsMatch(split) ||
                regexes[2].IsMatch(split) ||
                regexes[3].IsMatch(split))
                return true;
            return false;
        }

        static void PatchParam(string d, string f)
        {
            Console.Write("  Patching PARAM.SFO ...");
            try
            {
                var ParamStream = new FileStream(G.sourceDir + "\\PARAM.SFO", FileMode.Open, FileAccess.Write, FileShare.Read);
                var bStream = new BinaryWriter(ParamStream);
                var version = G.newVer.ToCharArray();
                ParamStream.Seek(G.verOffset, SeekOrigin.Begin);
                bStream.Write(version);
                bStream.Close();
                Green(d);
            }
            catch (Exception e)
            {
                Red(f);
                G.Exit("Error:\n" + e.Message);
            }
        }

        static void MakeNPData(string d, string f, string[] everyFile, string source, string LICPath)
        {
            var O = StringComparison.Ordinal;
            Console.Write("  Running make_npdata ...");
            try
            {
                using (var p = new System.Diagnostics.Process())
                {
                    p.StartInfo.FileName = G.makeNpdata;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.WorkingDirectory = G.currentDir;
                    foreach (string toConvert in everyFile)
                    {
                        if (toConvert == null)
                            continue;
                        var test = toConvert.Replace(source, "");
                        if (test.IndexOf("EBOOT"  , O) != -1 ||
                            test.IndexOf("LIC.DAT", O) != -1)
                            continue;
                        var dest = G.sourceDir + "\\" + test;
                        p.StartInfo.Arguments = "-e \"" + toConvert + "\" \"" + dest + "\" 0 1 3 0 16";
                        if (File.Exists(dest))
                            File.Delete(dest);
                        p.Start();
                        p.WaitForExit();
                    }
                    p.StartInfo.Arguments = "-e \"" + LICPath + "\" \"" + G.sourceDir
                        + "\\LICDIR\\LIC.EDAT\" 1 1 3 0 16 3 00 " + G.contentID + " 1";
                    p.Start();
                    p.WaitForExit();
                }
                Green(d);
            }
            catch (Exception e)
            {
                Red(f);
                G.Exit("Error:\n" + e.Message);
            }
        }

        static void GetContentID(string d, string f, string path)
        {
            Console.Write("  Extracting contentID ...");
            try
            {
                if (G.GenericCID)
                {
                    G.contentID = "EP9000 - " + G.newID + "_00-0000000000000001";
                }
                else
                {
                    using (var fs = File.OpenRead(path))
                    {
                        using (var bs = new BinaryReader(fs))
                        {
                            var cID = new StringBuilder(0x24);
                            fs.Seek(0x450, SeekOrigin.Begin);
                            var bytes = bs.ReadBytes(0x7);
                            foreach (byte b in bytes)
                                cID.Append(b);
                            cID.Append(G.newID);
                            fs.Seek(0x460, SeekOrigin.Begin);
                            bytes = bs.ReadBytes(0x14);
                            foreach (byte b in bytes)
                                cID.Append(b);
                            G.contentID = cID.ToString();
                        }
                    }
                }
                Green(d);
            }
            catch (Exception e)
            {
                Red(f);
                G.Exit("Error:\n" + e.Message);
            }
        }

        static void ProcessGameFiles(string LICPath)
        {
            Console.WriteLine("\nProcessing game files:");
            if (!Directory.Exists(G.sourceDir))
                Directory.CreateDirectory(G.sourceDir);
            string source = $@"{G.currentDir}\PS3_GAME\",
                d = " done\n", f = " failed\n";
            Console.Write("  Creating directory structure ...");
            try
            {
                foreach (string dirToCreate in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                {
                    var split = dirToCreate.Replace(source, "");
                    var realPath = $@"{G.sourceDir}\{split}";
                    if (!Directory.Exists(realPath))
                        Directory.CreateDirectory(realPath);
                }
                Green(d);
            }
            catch (Exception e)
            {
                Red(f);
                G.Exit("Error:\n" + e.Message);
            }
            var everyFile = Directory.GetFiles(source, "*.*", SearchOption.AllDirectories);
            Console.Write($"  {(G.CopyOnly ? "Copy" : "Mov")}ing content ...");
            var I = RegexOptions.IgnoreCase | RegexOptions.Compiled;
            Regex[] regexes = {
                new Regex(@"^TROPDIR\\", I),
                new Regex(@"^[^\\]+$", I),
                new Regex(@"^USRDIR\\.*?\.sprx$", I),
                new Regex(@"^USRDIR\\(EBOOT[^\\]+?\.BIN|[^\\]*?\.(edat|sdat))$", I)
            };
            var eboot = G.sourceDir + @"\USRDIR\EBOOT.BIN";
            try
            {
                for (int i = 0; i < everyFile.Length; ++i)
                {
                    var split = everyFile[i].Replace(source, "");
                    if (MoveTest(split, regexes))
                    {
                        var dest = G.sourceDir + "\\" + split;
                        if (File.Exists(dest))
                            File.Delete(dest);
                        if (G.CopyOnly)
                            File.Copy(everyFile[i], dest);
                        else
                            File.Move(everyFile[i], dest);
                        everyFile[i] = null;
                    }
                }
                if (File.Exists(eboot))
                    File.Delete(eboot);
                File.Copy($@"{G.outputDir}{G.ID}\USRDIR\EBOOT.BIN", eboot);
                Green(d);
            }
            catch (Exception e)
            {
                Red(f);
                G.Exit("Error:\n" + e.Message);
            }
            PatchParam(d, f);
            GetContentID(d, f, eboot);
            MakeNPData(d, f, everyFile, source, LICPath);
            if (!G.CopyOnly) {
                Console.Write("  Deleting source folder ...");
                try
                {
                    Directory.Delete(source, true);
                    Green(d);
                }
                catch (Exception e)
                {
                    Red(f);
                    G.Exit("Error:\n" + e.Message);
                }
            }
        }

        static void Green(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(msg);
            Console.ResetColor();
        }

        static void Red(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(msg);
            Console.ResetColor();
        }

        static void Cyan(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(msg);
            Console.ResetColor();
        }

        static void Help()
        {
            Console.WriteLine("Credits:");
            Cyan("mathieulh");
            Console.Write(" - PKG code, ");
            Cyan("Hykem");
            Console.WriteLine(" - make-npdata\n");
            Console.Write("To convert a game, please place the ");
            Green("PS3_GAME");
            Console.Write(" folder next to this program and\nrun it with no arguments or drag-n-drop a ");
            Green("PS3_GAME");
            Console.Write(" folder on the executable in\nWindows Explorer.\n\n" +
                "To check for compatibility, use the game's ID as an argument like so:\n");
            Red("   \"CFW2OFW Helper.exe\" ");
            Cyan("BLUS01234\n\n");
            Console.Write("Configuration:\n  Run the program once for it to create an INI file with default settings\n\n" +
                "    CopyFiles - ");
            Cyan("TRUE");
            Console.Write(" or ");
            Red("FALSE");
            Console.Write(" (default: ");
            Red("FALSE");
            Console.Write(")\n      If ");
            Cyan("TRUE");
            Console.Write(", then ");
            Green("PS3_GAME");
            Console.Write(" and its contents won't be modified\n\n" +
                "    PauseAfterConversion - ");
            Cyan("TRUE");
            Console.Write(" or ");
            Red("FALSE");
            Console.Write(" (default: ");
            Cyan("TRUE");
            Console.Write(")\n      If ");
            Cyan("TRUE");
            Console.Write(", then the program will pause after conversion\n\n" +
                "    UseGenericEbootCID - ");
            Cyan("TRUE");
            Console.Write(" or ");
            Red("FALSE");
            Console.Write(" (default: ");
            Red("FALSE");
            Console.Write(")\n      If ");
            Red("FALSE");
            Console.Write(", then the contentID from update will be used\n");
            G.Exit("", 0);
        }

        static void LICCheck(string LICPath, bool LICExists)
        {
            if (!LICExists)
            {
                Directory.CreateDirectory(G.currentDir + @"\PS3_GAME\LICDIR");
                Console.Write("LIC.DAT is missing.\nGenerating LIC.DAT ...");
                try
                {
                    GenerateLIC(LICPath, G.ID);
                    Green(" done\n");
                }
                catch (Exception)
                {
                    Red(" failed");
                    G.Exit("");
                }
            }
        }

        static void UpdatesCheck(bool exitAfterPatch)
        {
            var patch = G.xmlDoc.GetElementsByTagName("package");
            if (patch.Count > 0)
            {
                G.gameName = new Regex(@"[^A-Za-z0-9 _]", RegexOptions.Compiled).Replace(G.xmlDoc.GetElementsByTagName("TITLE").Item(0).InnerText, "");
                G.outputDir = $@"{G.currentDir}\{G.gameName.Replace(" ", "_")}_({G.ID})\";
                G.sourceDir = G.outputDir + G.newID;
                foreach (XmlNode package in patch)
                {
                    var url = package.Attributes["url"];
                    var sha1 = package.Attributes["sha1sum"];
                    if (url != null && sha1 != null)
                        G.patchURLs.Enqueue(new KeyValuePair<string, string>(url.Value, sha1.Value));
                    var size = package.Attributes["size"];
                    if (size != null)
                        G.size += UInt32.Parse(size.Value);
                }
                if (exitAfterPatch)
                {
                    Console.Write("Size of updates: ");
                    Green(G.size.ToString("N0"));
                    Console.Write(" bytes\n" + G.gameName + " [");
                    Cyan(G.ID);
                    Console.Write("] ");
                    Green("might be compatible");
                    G.Exit("", 0);
                }
                G.newVer = patch[patch.Count - 1].Attributes["version"].Value;
            }
            else
                G.Exit("No patches found.\n" + G.ID + " is not compatible with this hack.\n");
        }

        static void ProcessArgs(bool exitAfterPatch, string input)
        {
            var pattern = @"^B[LC][JUEAK][SM]\d{5}$";
            if (!new Regex(pattern, RegexOptions.Compiled).IsMatch(input))
                G.Exit("Invalid game ID: " + input);
            else
                G.ID = input;

            string lowID = G.ID.Substring(0, 2),
                regionID = G.ID.Substring(2, 1),
                highID = G.ID.Substring(4);
            var psnID = new StringBuilder("NP", 4);
            psnID.Append(regionID);
            psnID.Append(lowID == "BL" ? "B" : "A");
            G.newID = psnID.ToString() + highID;
            Console.Write("Game identified: ");
            Cyan(G.ID + "\n");
            if (!exitAfterPatch)
            {
                Console.Write("Target ID: ");
                Green(G.newID + "\n");
            }
            Console.Write("\n");
        }

        static void ParseSettings(out bool withoutEm)
        {
            string[] keys = { "CopyFiles", "PauseAfterConversion", "UseGenericEbootCID", "CheckForExclusiveMethod" };
            var Ini = new IniFile();
            withoutEm = false;

            string key = keys[0];
            if (Ini.KeyExists(key))
            {
                if (Ini.Read(key).Contains("true")) G.CopyOnly = true;
            }
            else
                Ini.Write(key, "False");
            
            key = keys[1];
            if (Ini.KeyExists(key))
            {
                if (Ini.Read(key).Contains("false")) G.Pause = true;
            }
            else
                Ini.Write(key, "True");

            key = keys[2];
            if (Ini.KeyExists(key))
            {
                if (Ini.Read(key).Contains("true")) G.GenericCID = true;
            }
            else
                Ini.Write(key, "False");

            key = keys[3];
            if (Ini.KeyExists(key))
            {
                if (Ini.Read(key).Contains("false")) withoutEm = true;
            }
            else
                Ini.Write(key, "True");
        }

        static int ShowEmMessage(int works, string note)
        {

            return 1;
        }

        static int CheckEm(string input, bool withoutEm)
        {
            if (withoutEm)
                return 0;
            EmJsonStructure[] EmList;
            var EmJson = "[{\"titleIds\":[\"BLES01697\"],\"works\":0,\"note\":\"Black screen after intro. [CFW2OFW Helper v8] [PS3GameConvert_V0.91] [Data Install] [SPRX]\"},{\"titleIds\":[\"BLUS31478\"],\"works\":1,\"note\":\"Works without patch.\"},{\"titleIds\":[\"BLUS30187\"],\"works\":0,\"note\":\"Game has one patch but doesn't contain EBOOT.BIN. UPDATE: Tested with BLJM60066 EBOOT and multiple variations of file structures.\"},{\"titleIds\":[\"BLES01763\"],\"works\":1,\"note\":\"Install Game Data before DTU. Creates \\\"/game/BLES01767/\\\" directory.\"},{\"titleIds\":[\"BLES02143\"],\"works\":1,\"note\":\"You must pre-install game data before DTU.\"},{\"titleIds\":[\"BLUS31207\"],\"works\":2,\"note\":\"You must pre-install game data before DTU.\"},{\"titleIds\":[\"BCES01123\",\"NPEA90127\",\"BCUS98298\"],\"works\":2,\"note\":\"BCUS98298 - Requires Demo EBOOT, Edit PARAM.SFO Category from DG Disc Game (blueray) to HG Harddrive Game, Game Conversion not required.\"},{\"titleIds\":[\"BLUS30629\"],\"works\":2,\"note\":\"Copy & replace all 42 .SPRX files located in USRDIR/BINARIES/PS3/XJOB/SHIPPING.\"},{\"titleIds\":[\"BLUS30386\"],\"works\":1,\"note\":\"BD contains INSDAT (update) & PKGDIR (DLC). You will need to manually extract all of them to play the DLC quests.\"},{\"titleIds\":[\"BLUS31270\"],\"works\":2,\"note\":\"Create a BLUS31270 folder and copy USRDIR from the full game directory. Use the game converter on it, and then navigate to NPUB31270/USRDIR and delete everything except EBOOT.bin. Copy all of the contents of the converted BLUS31270 into the BLUS31270 folder you had created, and overwrite all. Copy both your BLUS31270 and NPUB31270 to your OFW PS3.\"},{\"titleIds\":[\"BLES00148\"],\"works\":0,\"note\":\"Infinite Loading / Black Screen / Stuck At Confirm Screen using several different methods.\"},{\"titleIds\":[\"BLES00683\"],\"works\":2,\"note\":\"Copy all Disc files, except EBOOT.BIN, default.self, and default_mp.self from \\\"/USRDIR/*\\\" to update folder \\\"BLES00683/USRDIR/*\\\". Delete all converted files from \\\"NPEB00683/USRDIR/*\\\" except EBOOT.BIN from update. Copy default.self and default_mp.self from Disc to \\\"NPEB00683/USRDIR/\\\". NOTE: the game does not need converted if you manually copy the needed files from PS3_GAME folder, excluding USRDIR.\"},{\"titleIds\":[\"BLES01432\"],\"works\":2,\"note\":\"Move all Disc files (original files before converting) from USRDIR except EBOOT.BIN, default.self, and default_mp.self to update folder BLXXYYYYY/USRDIR/* then convert your game (USRDIR only contain EBOOT.BIN, default.self, and default_mp.self files)\"},{\"titleIds\":[\"BLES00404\"],\"works\":2,\"note\":\"The game with the patch and the modified PARAM.SFO must be thrown to another folder, for example BLES00404GAME, as well as the patch separately unchanged in the native folder (BLES00404).\"},{\"titleIds\":[\"BLUS30428\"],\"works\":2,\"note\":\"Use EBOOT from BLJM60215 update. Use NPJB60215 as conversion directory. If using CFW2OFW tool, change TitleID in PARAM.SFO to BLJM60215 and create new LIC.DAT before conversion. Install Game Data before DTU. Creates \\\"/game/BLJM60215DATA/\\\" directory. Buttons will be in Japanese format (X/O swapped) and some text will also be in Japanese (mostly English) because of EBOOT.\"},{\"titleIds\":[\"BLES01765\"],\"works\":2,\"note\":\"Fix \\\"creating save data\\\" loop: Convert game using ps3gameconvert or CFW2OFW Helper\"},{\"titleIds\":[\"BLES00723\"],\"works\":0,\"note\":\"Black screen after the Intro Logo's appear. [PS3GameConvert_V0.91]\"},{\"titleIds\":[\"BLUS30790\"],\"works\":1,\"note\":\"Use PS3GameConvert_v0.91 if you encounter error 8001003E.\"},{\"titleIds\":[\"BLJM61258\"],\"works\":1,\"note\":\"Use PS3GameConvert_v0.91 if you encounter graphical errors. JP games contains english language.\"},{\"titleIds\":[\"BLES00948\"],\"works\":1,\"note\":\"Use PS3GameConvert_v0.91 if you get stuck at the red logo screen.\"},{\"titleIds\":[\"BLUS30763\"],\"works\":2,\"note\":\"Use PS3GameConvert_v0.91 to avoid getting the game stuck at the red logo screen. Pre-install game data before DTU.\"},{\"titleIds\":[\"BLUS31396\"],\"works\":2,\"note\":\"Use EBOOT from BLJM61157 Update. Change TitleID in PARAM.SFO to BLJM61157 and create new LIC.DAT before conversion.\"},{\"titleIds\":[\"BLES00932\"],\"works\":2,\"note\":\"Use EBOOT from BCAS20071 update. Use NPHA20071 as conversion directory. If using CFW2OFW tool, change TitleID in PARAM.SFO to BCAS20071 and create new LIC.DAT before conversion.\"},{\"titleIds\":[\"BLES01698\",\"BLUS30723\"],\"works\":0,\"note\":\"Using default conversion causes freezing at initial auto-save. Replacing \\\"/USRDIR/BINARIES/NTJOBCODE/PS3/SUBMISSION/NTJOBCODE.PPU.SPRX\\\" from disc causes black screen. [Manual] [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BLES01287\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"/game/BLES01287INSTALL/\\\" directory.\"},{\"titleIds\":[\"BLES00452\"],\"works\":1,\"note\":\"Don't add DLC or you will encounter an error during trophy install making the game unplayable.\"},{\"titleIds\":[\"BLUS30977\"],\"works\":1,\"note\":\"Run game to generate dev_hdd0/game/BLUS30977_HDDCACHE/ directory before DTU.\"},{\"titleIds\":[\"BLES02064\"],\"works\":1,\"note\":\"Run game to generate dev_hdd0/game/BLES02064_HDDCACHE/ directory before DTU.\"},{\"titleIds\":[\"BLUS30645\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Creates \\\"/game/BLUS30645INSTALL/\\\" directory.\"},{\"titleIds\":[\"BLJM61090\"],\"works\":2,\"note\":\"Install Game Data from Disc Before DTU. Creates \\\"/game/NPJB00454/\\\" directory.\"},{\"titleIds\":[\"BLES01138\"],\"works\":2,\"note\":\"Install Game Data Before DTU. Also renamed converted directory to NPEA01138 to not conflict with Worms Ultimate Mayhem NPEB01138.\"},{\"titleIds\":[\"BLES02011\",\"BLUS31420\"],\"works\":2,\"note\":\"Delete \\\"patch_sound_english.dat\\\" in patch folder.\"},{\"titleIds\":[\"NPUA80001\"],\"works\":1,\"note\":\"Free on PSN.\"},{\"titleIds\":[\"BLES02080\"],\"works\":2,\"note\":\"Install Game Data from Disc before DTU.\"},{\"titleIds\":[\"BLUS30307\"],\"works\":1,\"note\":\"Install Game Data Before DTU.\"},{\"titleIds\":[\"BLUS30209\"],\"works\":2,\"note\":\"Use EBOOT from BLES00391 update. Use NPEB00391 as conversion directory. If using CFW2OFW tool, change TitleID in PARAM.SFO to BLES00391 and create new LIC.DAT before conversion.\"},{\"titleIds\":[\"NPUA80019\"],\"works\":1,\"note\":\"Free on PSN.\"},{\"titleIds\":[\"BLUS31452\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"/game/BLUS31452INSTALL/\\\" directory.\"},{\"titleIds\":[\"BLUS31588\"],\"works\":2,\"note\":\"Install Game Data from Disc before DTU.\"},{\"titleIds\":[\"BCUS98164\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Creates \\\"/game/BCUS98164DATA/\\\" directory.\"},{\"titleIds\":[\"BCES00802\",\"NPEA90076\"],\"works\":2,\"note\":\"Place the official patch into the /pkg folder of TABR and use \\\"Add a patch\\\" option when injecting. After restoring the backup, install the patch. Game can be played without Move controllers by disabling it in the game settings.\"},{\"titleIds\":[\"BLES00648\"],\"works\":2,\"note\":\"Use Demo EBOOT, PARAM.SFO and TITLE_ID from NPEB90167 and copy /USRDIR/* DISC files from BLES00648. Game loads and all stages are available. Note: Does not save data (?)\"},{\"titleIds\":[\"BLUS31405\",\"BLES01986\"],\"works\":0,\"note\":\"Black screen after the intro. Used EBOOT.BIN files from the demos but does not work.\"},{\"titleIds\":[\"BLES00254\"],\"works\":2,\"note\":\"Use Demo EBOOT, PARAM.SFO and TITLE_ID from NPUB90128 and copy /USRDIR/* DISC files from BLES00254\"},{\"titleIds\":[\"NPUA80012\",\"NPEA00004\"],\"works\":2,\"note\":\"Extract all files from NPUA80012 v2.01 DEMO. Delete all files from \\\"/USRDIR/*\\\" except EBOOT.BIN. Copy all files in \\\"/USRDIR/*\\\" from NPEA00004 v2.00, except EBOOT.BIN. Will display Trial Version on Title Screen and Nag after level completion, but all levels are available and functional.\"},{\"titleIds\":[\"BLES02102\"],\"works\":2,\"note\":\"Copy all .SPRX files located in USRDIR/master/prx and replace the ones in your converted game.\"},{\"titleIds\":[\"BLES01636\"],\"works\":2,\"note\":\"Install Game Data from Disc before DTU.\"},{\"titleIds\":[\"BLES02246\"],\"works\":2,\"note\":\"Copy & replace EBOOT.BIN of your converted game with the DEMO ver. then edit converted game PARAM.SFO Title ID to match the Demo ID and chancge Category to HG Harddrive Game.\"},{\"titleIds\":[\"NPEB90114\",\"BLES00322\",\"NPEB00052\"],\"works\":2,\"note\":\"Download the demo (NPEB90114) and take only the EBOOT.BIN and SPUJOBS.SPRX files from it, place them in the NPEB00052 folder and rename the folder to NPEB90114, and change the PARAM.SFO to NPEB90114.\"},{\"titleIds\":[\"BCUS01089\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Creates \\\"/game/BCUS01089_R/\\\" directory.\"},{\"titleIds\":[\"BCES00129\"],\"works\":2,\"note\":\"The game with the patch and the changed PARAM.SFO should be thrown to another folder, for example BCES00129GAME, as well as the patch separately unchanged in the native folder (BCES00129).\"},{\"titleIds\":[\"BLES01066\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Creates \\\"/game/BLES01066_INSTALL/\\\" directory.\"},{\"titleIds\":[\"BLJS10221\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"/game/BLJS10221_INSTALLDATA/\\\" directory.\"},{\"titleIds\":[\"BLJM61346\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"NPJB00769DATA\\\" directory. Tested with NPJB61346 DLC. This is NOT the english conversion and I did not experience any errors.\"},{\"titleIds\":[\"BLUS31410\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU.\"},{\"titleIds\":[\"BLUS30732\"],\"works\":2,\"note\":\"Replace all SPRX with DISC versions [/USRDIR/bin/*.sprx] and [/USRDIR/portal2/bin/*.sprx].\"},{\"titleIds\":[\"BLES00389\"],\"works\":0,\"note\":\"Black Screen using several different methods.\"},{\"titleIds\":[\"BLES00839\"],\"works\":0,\"note\":\"Black Screen using several different methods.\"},{\"titleIds\":[\"BLUS30485\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"/game/BLUS30485GAMEDATA/\\\" directory.\"},{\"titleIds\":[\"BLES01963\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Install DLC, DLC Fix, and Update, After Disc Data.\"},{\"titleIds\":[\"NPEB90505\",\"NPEB01356\"],\"works\":2,\"note\":\"The demo (NPEB90505) and the game (NPEB01356) are unpacked. Copy the ICON0.PNG and PIC1.PNG into it. In the folder USRDIR of the demo, delete everything except EBOOT.bin and copy everything from the full game's USRDIR into it except the EBOOT.bin. Delete cine_gameintro.pam and cine_gameintro_fr.pam. You can edit PARAM.SFO to have a better displayed name on the XMB.\"},{\"titleIds\":[\"BLES01179\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Creates \\\"/game/BLES00680\\\" directory.\"},{\"titleIds\":[\"BLUS30855\",\"BLES01465\"],\"works\":2,\"note\":\"Fix \\\"creating save data\\\" loop: Convert game using ps3gameconvert or CFW2OFW Helper\"},{\"titleIds\":[\"BLUS31444\"],\"works\":2,\"note\":\"Fix \\\"creating save data\\\" loop: Convert game using ps3gameconvert_v0.7 or CFW2OFW Helper (recommended)\"},{\"titleIds\":[\"BLES00373\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BLUS31205\"],\"works\":1,\"note\":\"Install Game Data Before DTU. Use PS3GameConvert_v0.91 if you experience any issues.\"},{\"titleIds\":[\"BLES00560\"],\"works\":0,\"note\":\"Black screen after Raven logo using CFW2OFW(v8). PS3GameConvert_v0.91 gives startup error. PS3GameConvert_v0.7 asks for disc to be inserted.\"},{\"titleIds\":[\"BCES01257\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BCES00894\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BCES00835\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BCES00494\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BCES00607\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BCES00265\"],\"works\":1,\"note\":\"Install Game Data Before DTU\"},{\"titleIds\":[\"BLUS30464\"],\"works\":2,\"note\":\"Install Game Data From Disc Before DTU. Creates \\\"/game/BLUS30464_INSTALL/\\\" directory.\"},{\"titleIds\":[\"NPEB01046\",\"NPUB90832\",\"BLUS30927\"],\"works\":2,\"note\":\"Download the demo (NPUB90832) and the full game (NPEB01046). Replace the full game's EBOOT.bin with the one from the demo and and change the Title ID in the full game PARAM.SFO to NPUB90832, rename the full game folder to NPUB90832 and inject the full game. BLUS30927 - Use PS3GameConvert_v0.91 and pre-install game data before DTU.\"},{\"titleIds\":[\"BLES01250\"],\"works\":0,\"note\":\"Insert Disc Error 8001003E. [Manual] [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BCES00819\"],\"works\":2,\"note\":\"Use EBOOT and TitleID from NPEA90112 Demo. Copy all files from disc /USRDIR/* to demo /USRDIR/*. Encrypt all *.TXT files under \\\"/USRDIR/SORCGAME/\\\" [PS3TOC.TXT, PS3TOC_ALL.TXT, etc] to \\\"*.TXT.SDAT\\\" using npdtool. I tested this up until the \\\"Connect Playstation Eye Camera\\\" message.\"},{\"titleIds\":[\"BLES01766\"],\"works\":2,\"note\":\"Google: Splinter Cell: Blacklist PS3 OFW BD Mirror FIX Tutorial by Blade.\"},{\"titleIds\":[\"BCES01598\"],\"works\":0,\"note\":\"Infinite Loading Screen. [Manual] [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BLUS30445\",\"BLUS30144\"],\"works\":2,\"note\":\"Edit the PARAM.SFO to BLUS30144 and copy the contents (except EBOOT.bin) of USRDIR into the patch file's USRDIR.\"},{\"titleIds\":[\"BLES00513\"],\"works\":0,\"note\":\"Game kicks you back to XMB. [PS3GameConvert_V0.91] [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BLES01371\",\"NPUB90713\"],\"works\":2,\"note\":\"Rename PS3_GAME to NPUB90713, copy PARAM.SFO and eboot.bin from the demo (NPUB90713) into NPUB90713. In order to change the language to Russian, rename SFXDESC and WAVES_PS3 from CONTENT_ENG to CONTENT_RUS, and remove CONTENT_ENG and rename the following: CONTENT_RUS to CONTENT_ENG, CONTENT_RUS.000.XTC to CONTENT_ENG.000.XTC and STRINGTABLE_RUS.XCR in STRINGTABLE_ENG.XCR. You can edit PARAM.SFO to have a better displayed name on the XMB.\"},{\"titleIds\":[\"BLES00289\"],\"works\":0,\"note\":\"Return To XMB. Tested with converted and normal SPRX.\"},{\"titleIds\":[\"BLES01982\"],\"works\":2,\"note\":\"Install Game Data from Disc before DTU.\"},{\"titleIds\":[\"BLES00159\",\"NPEB90036\",\"NPEB90049\"],\"works\":2,\"note\":\"Unpack the demo (NPEB90036 or NPEB90049), copy PIC1.PNG from the disc version into the demo. Delete all of the contents (except EBOOT.bin) of the USRDIR folder in the demo and copy it into the disc version of the game.\"},{\"titleIds\":[\"BLUS30125\"],\"works\":0,\"note\":\"Freezes During Ubisoft Logo [Pre-intall Game Data] [SPRX] [PS3GameConvert_V0.91] [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BLES00409\"],\"works\":0,\"note\":\"Frozen Black Screen. [PS3GameConvert_V0.91] [CFW2OFW Helper v8] UPD - because in patch there is link to dev_bd\"},{\"titleIds\":[\"BLUS30427\"],\"works\":2,\"note\":\"Install Game Data from Disc before DTU. Creates \\\"/game/BLUS30427DATA/\\\" directory.\"},{\"titleIds\":[\"NPEB90200\",\"NPEB00100\"],\"works\":2,\"note\":\"Unpack NPEB90200, delete everything except EBOOT.BIN in the folder USRDIR. Unpack NPEB00100 together with the patch, combine. Unpack the file data3.fbz with 7-zip into any folder (for example, data3). After you can delete it, it will no longer be needed. Next, open data1.fbz 7-zip'om and move the contents of the folder into which we unpacked data3.fbz. Move the contents of the folder USRDIR (which is in NPEB00100) into NPEB90200. You can edit PARAM.SFO to have a better displayed name on the XMB.\"},{\"titleIds\":[\"BLES01355\"],\"works\":1,\"note\":\"Pre-Install game data before DTU to avoid data install errors.\"},{\"titleIds\":[\"NPEB00108\"],\"works\":2,\"note\":\"Add the line \\\"trialmode = false\\\" to the file local_config.txt (without quotes)\"},{\"titleIds\":[\"BCES00225\"],\"works\":2,\"note\":\"Do not DTU the patch folder (BCES00225) or you will encounter a corrupt game data error. Game works without a patch or you can patch the game online after DTU. [CFW2OFW Helper v8]\"},{\"titleIds\":[\"BCES00664\"],\"works\":2,\"note\":\"Rename patch to BCES00664DATA, delete the entire contents of its USRDIR folder, copy all dataXX.psarc to it from the disc. Edit the PARAM.SFO and remove the lines: PS3 System, Parental Lock Level, App Ver and Target Ver. Patch 2.10 (again) with the modified PARAM.SFO (HG and APP Ver 2.51) in the original folder (BCES00664) or in BCES00664GAME + DFEngine.sprx from patch 2.30. [CFW2OFW Helper v8]\"}]";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(EmJson)))
            {
                var parsedJson = new DataContractJsonSerializer(typeof(EmJsonStructure[]));
                EmList = parsedJson.ReadObject(ms) as EmJsonStructure[];
            }
            foreach (var game in EmList)
            {
                foreach (var title in game.titleIds)
                {
                    if (title == input)
                    {
                        return ShowEmMessage(game.works, game.note);
                    }
                }
            }
            return 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (!File.Exists(G.makeNpdata))
                G.Exit("Missing make_npdata.exe");
            string ParamPath = G.currentDir + @"\PS3_GAME\PARAM.SFO",
                LICPath = G.currentDir + @"\PS3_GAME\LICDIR\LIC.DAT";
            var ParamExists = File.Exists(ParamPath);
            var LICExists = File.Exists(LICPath);
            var exitAfterPatch = false;
            var input = new StringBuilder(9);
            bool withoutEm = false;
            if (G.NoCheck)
            {
                ServicePointManager.ServerCertificateValidationCallback += delegate { return true; };
                WebRequest.DefaultWebProxy = null;
                G.wc.Proxy = null;
                ParseSettings(out withoutEm);
                Console.WriteLine(" --- CFW2OFW Helper v9 ---\n// https://github.com/friendlyanon/CFW2OFW-Helper/");
            }
            switch (args.Length)
            {
            case 0:
                if (ParamExists)
                {
                    try
                    {
                        input.Append(ProcessParam(ParamPath));
                    }
                    catch (Exception ex)
                    {
                        G.Exit("An error occured while trying to read PARAM.SFO:\n" + ex.Message);
                    }
                }
                else
                    Help();
                break;
            case 1:
                switch (args[0])
                {
                case "help":
                case "-help":
                case "--help":
                case "/?":
                case "-h":
                case "/h":
                    Help();
                    break;
                default:
                    if (args[0].Length == 9)
                        G.hasEm = CheckEm(args[0], withoutEm);
                    var DropRegex = new Regex($@"\\PS3_GAME\\?{"\""}?$", RegexOptions.Compiled);
                    if (DropRegex.IsMatch(args[0]))
                    {
                        G.currentDir = DropRegex.Replace(args[0], "");
                        G.NoCheck = false;
                        return Main(new string[] { });
                    }
                    else
                    {
                        input.Append(args[0]);
                        exitAfterPatch = true;
                    }
                    break;
                }
                break;
            default:
                G.Exit("Too many arguments!");
                break;
            }
            ProcessArgs(exitAfterPatch, input.ToString());
            Updates();
            UpdatesCheck(exitAfterPatch);
            if (!Directory.Exists(G.patchPath))
                Directory.CreateDirectory(G.patchPath);
            LICCheck(LICPath, LICExists);
            GetPatches();
            unchecked {
                ProcessPatches();
            }
            ProcessGameFiles(LICPath);
            Console.Write("\n");
            if (G.Pause)
            {
                Console.Write("Press any key to exit . . .");
                Console.ReadKey(true);
                Console.Write(" Exiting");
            }
            return 0;
        }
    }
}
