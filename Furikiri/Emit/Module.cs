﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Furikiri.Emit
{
    /// <summary>
    /// Compiled Script
    /// </summary>
    public class Module : ISourceAccessor
    {
        private const string FILE_TAG_LE = "TJS2";
        private const string VER_TAG_LE = "100";
        private const string OBJ_TAG_LE = "OBJS";
        private const string DATA_TAG_LE = "DATA";
        private const int MIN_BYTE_COUNT = 64;
        private const int MIN_SHORT_COUNT = 64;
        private const int MIN_INT_COUNT = 64;
        private const int MIN_DOUBLE_COUNT = 8;
        private const int MIN_LONG_COUNT = 8;
        private const int MIN_STRING_COUNT = 1024;

        private static readonly string NO_SCRIPT = "no script";

        private byte[] _bytes;
        private int[] _ints;
        private short[] _shorts;
        private long[] _longs;
        private double[] _doubles;
        private string[] _strings;
        private List<byte[]> _octets;
        private short[] _varTypes;

        public void LoadFromFile(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                // TJS2
                var tag = br.ReadChars(4).ToRealString();
                if (tag != FILE_TAG_LE)
                {
                    return;
                }

                // 100'\0'
                if (br.ReadChars(3).ToRealString() != VER_TAG_LE)
                {
                    return;
                }
                br.ReadChar();

                //file size
                int size = br.ReadInt32();
                if (size != fs.Length)
                {
                    Debug.WriteLine($"File size incorrect: Expect {size}, Actual {fs.Length}");
                }

                if (br.ReadChars(4).ToRealString() != DATA_TAG_LE)
                {
                    return;
                }

                size = br.ReadInt32();
                ReadDataArea(br, size);

                // OBJS
                if (br.ReadChars(4).ToRealString() != OBJ_TAG_LE)
                {
                    return;
                }
                ReadObjects(br);
            }

            return;
        }

        private void ReadObjects(BinaryReader br)
        {
            int totalSize = br.ReadInt32();
            int topLevel = br.ReadInt32();
            int objCount = br.ReadInt32();

            int[] parents = new int[objCount];
            int[] propSetters = new int[objCount];
            int[] propGetters = new int[objCount];
            int[] superClassGetters = new int[objCount];
            List<int[]> properties = new List<int[]>(objCount);
            List<(ITjsVariant Var, int Index)> replacements = new List<(ITjsVariant Var, int Index)>();

            for (int i = 0; i < objCount; i++)
            {
                if (br.ReadChars(4).ToRealString() != FILE_TAG_LE)
                {
                    throw new TjsFormatException("ByteCode error");
                }

                int objSize = br.ReadInt32();
                parents[i] = br.ReadInt32();
                int name = br.ReadInt32();
                int contextType = br.ReadInt32();
                int maxVariableCount = br.ReadInt32();
                int variableReserveCount = br.ReadInt32();
                int maxFrameCount = br.ReadInt32();
                int funcDeclArgCount = br.ReadInt32();
                int funcDeclUnnamedArgArrayBase = br.ReadInt32();
                int funcDeclCollapseBase = br.ReadInt32();
                propSetters[i] = br.ReadInt32();
                propGetters[i] = br.ReadInt32();
                superClassGetters[i] = br.ReadInt32();
                //codePos/srcPos
                int count = br.ReadInt32();
                long[] srcPos = new long[count];
                for (int j = 0; j < count; j++)
                {
                    srcPos[j] = br.ReadInt64();
                }

                //code
                count = br.ReadInt32();
                short[] code = new short[count];
                for (int j = 0; j < count; j++)
                {
                    code[j] = br.ReadInt16();
                }

                var padding = 4 - (count * 2) % 4;
                if (padding > 0 && padding < 4)
                {
                    br.ReadBytes(padding);
                }

                //var
                count = br.ReadInt32();
                int vCount = count * 2;
                if (_varTypes == null || _varTypes.Length < vCount)
                {
                    _varTypes = new short[vCount];
                }

                for (int j = 0; j < vCount; j++)
                {
                    _varTypes[j] = br.ReadInt16();
                }

                List<ITjsVariant> vars = new List<ITjsVariant>(count);

                for (int j = 0; j < count; j++)
                {
                    int p = j * 2;
                    int type = _varTypes[p];
                    int index = _varTypes[p + 1];
                    switch ((TjsInternalType)type)
                    {
                        case TjsInternalType.Void:
                            vars[j] = TjsVoid.Void;
                            break;
                        case TjsInternalType.Object:
                            vars[j] = new TjsObject(null);
                            break;
                        case TjsInternalType.InterObject:
                            vars[j] = new TjsObject(null) { Internal = true };
                            replacements.Add((vars[j], index));
                            break;
                        case TjsInternalType.InterGenerator:
                            //TODO:
                            break;
                        case TjsInternalType.Octet:
                            break;
                        case TjsInternalType.Int:
                            break;
                        case TjsInternalType.Real:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }
        }

        private void ReadDataArea(BinaryReader br, int size)
        {
            //byte
            int count = br.ReadInt32();
            if (_bytes == null || _bytes.Length < count)
            {
                _bytes = new byte[Math.Max(count, MIN_BYTE_COUNT)];
            }

            if (count > 0)
            {
                br.ReadBytes(count).CopyTo(_bytes, 0);
                //padding
                var padding = 4 - count % 4;
                if (padding > 0 && padding < 4)
                {
                    br.ReadBytes(padding);
                }
            }

            //short
            count = br.ReadInt32();
            if (_shorts == null || _shorts.Length < count)
            {
                _shorts = new short[Math.Max(count, MIN_SHORT_COUNT)];
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    _shorts[i] = br.ReadInt16();
                }
                //padding
                var padding = 4 - (count * 2) % 4;
                if (padding > 0 && padding < 4)
                {
                    br.ReadBytes(padding);
                }
            }

            //int
            count = br.ReadInt32();
            if (_ints == null || _ints.Length < count)
            {
                _ints = new int[Math.Max(count, MIN_INT_COUNT)];
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    _ints[i] = br.ReadInt32();
                }
            }

            //long
            count = br.ReadInt32();
            if (_longs == null || _longs.Length < count)
            {
                _longs = new long[Math.Max(count, MIN_LONG_COUNT)];
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    _longs[i] = br.ReadInt64();
                }
            }

            //double
            count = br.ReadInt32();
            if (_doubles == null || _doubles.Length < count)
            {
                _doubles = new double[Math.Max(count, MIN_DOUBLE_COUNT)];
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    _doubles[i] = br.ReadDouble();
                }
            }

            //string
            count = br.ReadInt32();
            if (_strings == null || _strings.Length < count)
            {
                _strings = new string[Math.Max(count, MIN_STRING_COUNT)];
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var str = br.Read2ByteString();
                    //TODO: Tjs.MapGlobalStringMap(new string(ch));
                    _strings[i] = str;
                    var padding = 4 - (str.Length * 2) % 4;
                    if (padding > 0 && padding < 4)
                    {
                        br.ReadBytes(padding);
                    }
                }
            }

            //octet
            count = br.ReadInt32();
            if (_octets == null || _octets.Count < count)
            {
                _octets = new List<byte[]>();
            }

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    int len = br.ReadInt32();
                    _octets.Add(br.ReadBytes(len));
                    //padding
                    var padding = 4 - count % 4;
                    if (padding > 0 && padding < 4)
                    {
                        br.ReadBytes(padding);
                    }
                }
            }

        }
    }
}
