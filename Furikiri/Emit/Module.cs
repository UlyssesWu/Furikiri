using System;
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

        private const string NO_SCRIPT = "no script";

        private byte[] _bytes;
        private int[] _ints;
        private short[] _shorts;
        private long[] _longs;
        private double[] _doubles;
        private string[] _strings;
        private List<byte[]> _octets;
        private short[] _varTypes;

        public CodeObject TopLevel { get; set; }
        public List<CodeObject> Objects { get; set; }

        public Module(string path)
        {
            LoadFromFile(path);
        }

        public void LoadFromStream(Stream stream)
        {
            using (var br = new BinaryReader(stream))
            {
                // TJS2
                var tag = br.ReadChars(4).ToRealString();
                if (tag != FILE_TAG_LE)
                {
                    throw new TjsFormatException(TjsBadFormatReason.Header, "Signature wrong");
                }

                // 100'\0'
                var ver = br.ReadChars(3).ToRealString();
                if (ver != VER_TAG_LE)
                {
                    Debug.WriteLine($"Unknown version: {ver}");
                    //throw new TjsFormatException(TjsBadFormatReason.Version, "Version unsupported");
                }

                br.ReadChar();

                //file size
                int size = br.ReadInt32();
                if (size != stream.Length)
                {
                    Debug.WriteLine($"File size incorrect: Expect {size}, Actual {stream.Length}");
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
        }

        public void LoadFromFile(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                LoadFromStream(fs);
            }
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
            List<(TjsCodeObject Var, int Index)> replacements = new List<(TjsCodeObject Var, int Index)>();
            List<CodeObject> objects = new List<CodeObject>(objCount);

            for (int i = 0; i < objCount; i++)
            {
                if (br.ReadChars(4).ToRealString() != FILE_TAG_LE)
                {
                    throw new TjsFormatException(TjsBadFormatReason.Header, "ByteCode Signature error");
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
                    var type = _varTypes[p];
                    int index = _varTypes[p + 1];
                    switch ((TjsInternalType) type)
                    {
                        case TjsInternalType.Void:
                            vars.Add(TjsVoid.Void);
                            break;
                        case TjsInternalType.Object:
                            vars.Add(new TjsObject(null) {Internal = false});
                            break;
                        case TjsInternalType.InterObject:
                            var co = new TjsCodeObject(null) {Internal = true};
                            vars.Add(co);
                            replacements.Add((co, index));
                            break;
                        case TjsInternalType.InterGenerator:
                            var co2 = new TjsCodeObject(null) {Internal = true};
                            vars.Add(co2);
                            replacements.Add((co2, index));
                            break;
                        case TjsInternalType.String:
                            vars.Add(new TjsString(_strings[index]));
                            break;
                        case TjsInternalType.Octet:
                            vars.Add(new TjsOctet(_octets[index]));
                            break;
                        case TjsInternalType.Real:
                            vars.Add(new TjsReal(_doubles[index]));
                            break;
                        case TjsInternalType.Byte:
                            vars.Add(new TjsInt(_bytes[index]));
                            break;
                        case TjsInternalType.Short:
                            vars.Add(new TjsInt(_shorts[index]));
                            break;
                        case TjsInternalType.Int:
                            vars.Add(new TjsInt(_ints[index]));
                            break;
                        case TjsInternalType.Long:
                            vars.Add(new TjsReal(_longs[index]));
                            break;

                        case TjsInternalType.Unknown:
                        default:
                            vars.Add(TjsVoid.Void);
                            break;
                    }
                }

                count = br.ReadInt32();
                int[] superPointers = new int[count];
                for (int j = 0; j < count; j++)
                {
                    superPointers[j] = br.ReadInt32();
                }

                //properties
                count = br.ReadInt32();
                int[] props = new int[count * 2];
                for (int j = 0; j < count * 2; j++)
                {
                    props[j] = br.ReadInt32();
                }

                properties.Add(props);

                CodeObject obj = new CodeObject(this, _strings[name], contextType, code, vars, maxVariableCount,
                    variableReserveCount, maxFrameCount, funcDeclArgCount, funcDeclUnnamedArgArrayBase,
                    funcDeclCollapseBase, true, srcPos, superPointers);

                objects.Add(obj);
            }

            TjsCodeObject propVar = null;
            for (int i = 0; i < objCount; i++)
            {
                CodeObject parent = null;
                CodeObject propSetter = null;
                CodeObject propGetter = null;
                CodeObject superClassGetter = null;

                if (parents[i] >= 0)
                {
                    parent = objects[parents[i]];
                }

                if (propSetters[i] >= 0)
                {
                    propSetter = objects[propSetters[i]];
                }

                if (propGetters[i] >= 0)
                {
                    propGetter = objects[propGetters[i]];
                }

                if (superClassGetters[i] >= 0)
                {
                    superClassGetter = objects[superClassGetters[i]];
                }

                objects[i].Parent = parent;
                objects[i].Setter = propSetter;
                objects[i].Getter = propGetter;
                objects[i].SuperClass = superClassGetter;

                if (properties[i] != null)
                {
                    var pObj = parent;
                    var props = properties[i];
                    for (int j = 0; j < props.Length / 2; j++)
                    {
                        var name = _strings[props[j * 2]];
                        var obj = objects[props[j * 2 + 1]];
                        propVar = new TjsCodeObject(obj);
                        pObj?.SetProperty(TjsInterfaceFlag.MemberEnsure | TjsInterfaceFlag.IgnorePropInvoking, name,
                            propVar, obj);
                    }
                }
            }

            foreach (var replacement in replacements)
            {
                replacement.Var.Object = objects[replacement.Index];
            }

            replacements.Clear();
            if (topLevel >= 0)
            {
                TopLevel = objects[topLevel];
            }

            Objects = objects;
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