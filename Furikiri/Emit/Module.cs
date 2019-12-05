using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Furikiri.Emit
{
    /// <summary>
    /// Compiled Script
    /// </summary>
    public class Module : ISourceAccessor
    {
        internal const string FILE_TAG_LE = "TJS2";
        internal const string VER_TAG_LE = "100";
        internal const string OBJS_TAG_LE = "OBJS";
        internal const string DATA_TAG_LE = "DATA";

        private const string NO_SCRIPT = "no script";

        private DataSection Data { get; set; } = new DataSection();

        public CodeObject TopLevel { get; set; }
        public List<CodeObject> Objects { get; set; }
        public Dictionary<CodeObject, Method> Methods { get; set; } = new Dictionary<CodeObject, Method>();
        public Dictionary<string, Property> Properties { get; set; } = new Dictionary<string, Property>();

        public void Resolve()
        {
            Methods = new Dictionary<CodeObject, Method>();
            Properties = new Dictionary<string, Property>();

            //Method
            foreach (var method in Objects.Where(obj =>
                obj.ContextType == TjsContextType.TopLevel || obj.ContextType == TjsContextType.PropertyGetter ||
                obj.ContextType == TjsContextType.PropertySetter || obj.ContextType == TjsContextType.Function ||
                obj.ContextType == TjsContextType.ExprFunction))
            {
                Methods[method] = method.ResolveMethod();
            }

            //Property
            foreach (var prop in Objects.Where(obj => obj.ContextType == TjsContextType.Property))
            {
                Properties[prop.Name] = prop.ResolveProperty(Methods.TryGet(prop.Getter), Methods.TryGet(prop.Setter));
            }
        }

        public Module(string path)
        {
            LoadFromFile(path);
        }

        public void LoadFromStream(Stream stream)
        {
            using var br = new BinaryReader(stream);
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

            bool dataLoaded = false;
            bool objsLoaded = false;

            while (br.PeekChar() != -1 && (!dataLoaded || !objsLoaded))
            {
                tag = br.ReadChars(4).ToRealString();
                size = br.ReadInt32();
                if (tag == DATA_TAG_LE && !dataLoaded) //DATA
                {
                    Data = new DataSection();
                    Data.Read(br);
                    dataLoaded = true;
                }
                else if (tag == OBJS_TAG_LE && !objsLoaded) //OBJS
                {
                    ReadObjects(br);
                    objsLoaded = true;
                }
                else
                {
                    br.BaseStream.Seek(size, SeekOrigin.Current);
                }
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

                int objSize = br.ReadInt32(); // indicate the following size, objSize not included
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
                if (Data.VarTypes == null)
                {
                    Data.VarTypes = new List<short>(vCount);
                }

                for (int j = 0; j < vCount; j++)
                {
                    Data.VarTypes.Add(br.ReadInt16());
                }

                List<ITjsVariant> vars = new List<ITjsVariant>(count);

                for (int j = 0; j < count; j++)
                {
                    int p = j * 2;
                    var type = Data.VarTypes[p];
                    int index = Data.VarTypes[p + 1];
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
                            vars.Add(new TjsString(Data.Strings[index]));
                            break;
                        case TjsInternalType.Octet:
                            vars.Add(new TjsOctet(Data.Octets[index]));
                            break;
                        case TjsInternalType.Real:
                            vars.Add(new TjsReal(Data.Doubles[index]));
                            break;
                        case TjsInternalType.Byte:
                            vars.Add(new TjsInt(Data.Bytes[index]));
                            break;
                        case TjsInternalType.Short:
                            vars.Add(new TjsInt(Data.Shorts[index]));
                            break;
                        case TjsInternalType.Int:
                            vars.Add(new TjsInt(Data.Ints[index]));
                            break;
                        case TjsInternalType.Long:
                            vars.Add(new TjsReal(Data.Longs[index]));
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

                CodeObject obj = new CodeObject(this, Data.Strings[name], contextType, code, vars, maxVariableCount,
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
                        var name = Data.Strings[props[j * 2]];
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

        private void WriteObjects(BinaryWriter bw)
        {
            if (Objects == null)
            {
                Objects = new List<CodeObject>();
            }

            var totalSizePos = bw.BaseStream.Position;
            bw.Write(0); //totalSize stub
            bw.Write(Objects.GetOrAddIndex(TopLevel)); //TopLevel index
            bw.Write(Objects.Count); //object count

            for (var i = 0; i < Objects.Count; i++) //it works only if you do not remove from the list
            {
                var codeObject = Objects[i];
                bw.Write(FILE_TAG_LE);
                WriteObject(bw, codeObject);
            }
        }

        private void WriteObject(BinaryWriter bw, CodeObject obj)
        {
            var objSizePos = bw.BaseStream.Position;
            bw.Write(0); //objSize stub
            bw.Write(Objects.GetOrAddIndex(obj.Parent));
        }

        public void WriteToStream(Stream stream)
        {
            using var bw = new BinaryWriter(stream);
            bw.Write(FILE_TAG_LE.ToCharArray());
            bw.Write(VER_TAG_LE.ToCharArray());
            bw.Write('\0');

            var fileSizePos = bw.BaseStream.Position;
            bw.Write(0); //fileSize stub

            //DATA
            bw.Write(DATA_TAG_LE.ToCharArray());
            var dataSizePos = bw.BaseStream.Position;
            bw.Write(0); //dataSize stub
            Data.Write(bw);
            var dataSize = (int) (bw.BaseStream.Position - dataSizePos - 4);
            bw.WriteAndJumpBack(dataSize, dataSizePos);

            //OBJS
            bw.Write(OBJS_TAG_LE.ToCharArray());
            dataSizePos = bw.BaseStream.Position;
            bw.Write(0); //dataSize stub
            WriteObjects(bw);
            dataSize = (int)(bw.BaseStream.Position - dataSizePos - 4);
            bw.WriteAndJumpBack(dataSize, dataSizePos);
        }
    }
}