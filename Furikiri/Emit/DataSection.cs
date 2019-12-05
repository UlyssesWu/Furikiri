using System;
using System.Collections.Generic;
using System.IO;

namespace Furikiri.Emit
{
    public class DataSection
    {
        internal const int MIN_BYTE_COUNT = 64;
        internal const int MIN_SHORT_COUNT = 64;
        internal const int MIN_INT_COUNT = 64;
        internal const int MIN_DOUBLE_COUNT = 8;
        internal const int MIN_LONG_COUNT = 8;
        internal const int MIN_STRING_COUNT = 1024;

        public List<byte> Bytes;
        public List<int> Ints;
        public List<short> Shorts;
        public List<long> Longs;
        public List<double> Doubles;
        public List<string> Strings;
        public List<byte[]> Octets;
        public List<short> VarTypes;

        public DataSection(bool init = false)
        {
            if (init)
            {
                Bytes = new List<byte>();
                Ints = new List<int>();
                Shorts = new List<short>();
                Longs = new List<long>();
                Doubles = new List<double>();
                Strings = new List<string>();
                Octets = new List<byte[]>();
                VarTypes = new List<short>();
            }
        }

        public void Read(BinaryReader br)
        {
            //byte
            int count = br.ReadInt32();
            if (Bytes == null)
            {
                Bytes = new List<byte>(Math.Max(count, MIN_BYTE_COUNT));
            }

            if (count > 0)
            {
                Bytes.AddRange(br.ReadBytes(count));
                //padding
                br.ReadPadding(count, sizeof(byte));
            }

            //short
            count = br.ReadInt32();
            if (Shorts == null)
            {
                Shorts = new List<short>(Math.Max(count, MIN_SHORT_COUNT));
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Shorts.Add(br.ReadInt16());
                }

                //padding
                br.ReadPadding(count, sizeof(short));
            }

            //int
            count = br.ReadInt32();
            if (Ints == null)
            {
                Ints = new List<int>(Math.Max(count, MIN_INT_COUNT));
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Ints.Add(br.ReadInt32());
                }
            }

            //long
            count = br.ReadInt32();
            if (Longs == null)
            {
                Longs = new List<long>(Math.Max(count, MIN_LONG_COUNT));
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Longs.Add(br.ReadInt64());
                }
            }

            //double
            count = br.ReadInt32();
            if (Doubles == null)
            {
                Doubles = new List<double>(Math.Max(count, MIN_DOUBLE_COUNT));
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Doubles.Add(br.ReadDouble());
                }
            }

            //string
            count = br.ReadInt32();
            if (Strings == null)
            {
                Strings = new List<string>(Math.Max(count, MIN_STRING_COUNT));
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var str = br.Read2ByteString();
                    Strings.Add(str);
                    //TODO: Tjs.MapGlobalStringMap(new string(ch));
                    br.ReadPadding(str.Length, sizeof(char));
                }
            }

            //octet
            count = br.ReadInt32();
            if (Octets == null)
            {
                Octets = new List<byte[]>(count);
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int len = br.ReadInt32();
                    Octets.Add(br.ReadBytes(len));
                    //padding
                    br.ReadPadding(len, sizeof(byte));
                }
            }
        }

        public void Write(BinaryWriter bw)
        {
            //byte
            bw.Write(Bytes.Count);
            Bytes.ForEach(bw.Write);
            bw.WritePadding(Bytes.Count);

            //short
            bw.Write(Shorts.Count);
            Shorts.ForEach(bw.Write);
            bw.WritePadding(Shorts.Count, sizeof(short));

            //int
            bw.Write(Ints.Count);
            Ints.ForEach(bw.Write);
            
            //long
            bw.Write(Longs.Count);
            Longs.ForEach(bw.Write);

            //string
            bw.Write(Strings.Count);
            Strings.ForEach(bw.Write2ByteString);
            bw.WritePadding(Strings.Count, sizeof(char));

            //octet
            bw.Write(Octets.Count);
            Octets.ForEach(bytes =>
            {
                bw.Write(bytes);
                bw.WritePadding(bytes.Length);
            });
        }
    }
}