using System.Text;
using SharpGraph.Graph;

namespace SharpGraph.Persistence;

/// <summary>
/// Serialización binaria de <see cref="FileFragment"/>: el formato de la caché v2.
///
/// El JSON de la v1 obligaba a deserializar megas de texto y repetía cada FQN
/// en cada arista; aquí TODAS las cadenas van en una tabla única deduplicada al
/// inicio del fichero y el cuerpo solo referencia índices varint. El mismo grafo
/// ocupa ~5-10× menos y se carga sin parsear texto.
///
/// Layout (little-endian):
///   "SGC1" | formatVersion u32 | parserVersion u32 | fragmentCount u32
///   tabla de strings: count varint | (len varint + UTF-8)*
///   fragmentos × fragmentCount (índices varint a la tabla; 0 = null)
/// </summary>
internal static class FragmentBinary
{
    private const int FormatVersion = 1;

    // ------------------------------------------------------------- escritura

    public static byte[] Write(IReadOnlyCollection<FileFragment> fragments, int parserVersion)
    {
        var body = new MemoryStream();
        var table = new List<string>(8 * 1024) { "\0" }; // índice 0 reservado = null
        var ids = new Dictionary<string, int>(8 * 1024, StringComparer.Ordinal);

        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var f in fragments)
            {
                w.Write7(Str(f.FilePath));
                w.Write7(Str(f.Hash));
                w.Write7(f.Usings.Count);
                foreach (var u in f.Usings) w.Write7(Str(u));
                w.Write7(f.Aliases.Count);
                foreach (var (alias, target) in f.Aliases)
                {
                    w.Write7(Str(alias));
                    w.Write7(Str(target));
                }

                w.Write7(f.Nodes.Count);
                foreach (var n in f.Nodes)
                {
                    w.Write7(Str(n.Name));
                    w.Write7(Str(n.Namespace));
                    w.Write((byte)n.Kind);
                    w.Write(n.IsPublic ? (byte)1 : (byte)0);
                    w.Write7(n.StartLine);
                    w.Write7(n.EndLine);
                    w.Write7(n.Summary is null ? 0 : Str(n.Summary));
                }

                w.Write7(f.Edges.Count);
                foreach (var e in f.Edges)
                {
                    w.Write7(Str(e.From));
                    w.Write(e.FromResolved ? (byte)1 : (byte)0);
                    w.Write7(Str(e.To));
                    w.Write(e.ToResolved ? (byte)1 : (byte)0);
                    w.Write7(Str(e.Ns));
                    w.Write((byte)e.Relation);
                    w.Write7(e.Line);
                    w.Write7(e.FromMember is null ? 0 : Str(e.FromMember));
                }

                w.Write7(f.Endpoints.Count);
                foreach (var ep in f.Endpoints)
                {
                    w.Write7(Str(ep.TypeName));
                    w.Write7(Str(ep.Verb));
                    w.Write7(Str(ep.Route));
                    w.Write7(Str(ep.MethodName));
                    w.Write7(ep.Line);
                }

                w.Write7(f.CallSites.Count);
                foreach (var cs in f.CallSites)
                {
                    w.Write7(Str(cs.CallerType));
                    w.Write7(Str(cs.CallerMember));
                    w.Write7(Str(cs.CalleeType));
                    w.Write7(Str(cs.CalleeMember));
                    w.Write7(Str(cs.Ns));
                    w.Write7(cs.Line);
                }

                w.Write7(f.DiBindings.Count);
                foreach (var di in f.DiBindings)
                {
                    w.Write7(Str(di.ServiceType));
                    w.Write7(Str(di.ImplementationType));
                    w.Write7(Str(di.Lifetime));
                    w.Write7(Str(di.Ns));
                    w.Write7(di.Line);
                }

                w.Write7(f.Members.Count);
                foreach (var m in f.Members)
                {
                    w.Write7(Str(m.TypeName));
                    w.Write7(Str(m.MemberName));
                    w.Write7(Str(m.Kind));
                    w.Write7(Str(m.Signature));
                    w.Write7(m.StartLine);
                    w.Write7(m.EndLine);
                    w.Write(m.IsPublic ? (byte)1 : (byte)0);
                }

                w.Write7(f.ReturnSignatures.Count);
                foreach (var s in f.ReturnSignatures)
                {
                    w.Write7(Str(s.TypeName));
                    w.Write7(Str(s.MemberName));
                    w.Write((byte)s.Kind);
                    w.Write7(Str(s.ReturnSimpleType));
                    w.Write7(Str(s.Ns));
                }

                w.Write7(f.PendingCallSites.Count);
                foreach (var pcs in f.PendingCallSites)
                {
                    w.Write7(Str(pcs.CallerType));
                    w.Write7(Str(pcs.CallerMember));
                    w.Write7(Str(pcs.CalleeMember));
                    WriteSteps(w, pcs.Receiver, table, ids);
                    w.Write7(Str(pcs.Ns));
                    w.Write7(pcs.Line);
                }

                w.Write7(f.PendingLocals.Count);
                foreach (var pl in f.PendingLocals)
                {
                    w.Write7(Str(pl.DeclaringType));
                    w.Write7(Str(pl.DeclaringMember));
                    w.Write7(Str(pl.LocalName));
                    WriteSteps(w, pl.Initializer, table, ids);
                    w.Write7(Str(pl.Ns));
                }
            }
        }

        // composición final: header + tabla de strings + cuerpo
        var ms = new MemoryStream(64 + table.Count * 24 + (int)body.Length);
        using (var head = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            head.Write("SGC1"u8);
            head.Write(FormatVersion);
            head.Write(parserVersion);
            head.Write(fragments.Count);
            head.Write7(table.Count - 1); // sin el slot reservado
            foreach (var s in table.Skip(1))
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                head.Write7(bytes.Length);
                head.Write(bytes);
            }
            body.Position = 0;
            body.CopyTo(ms);
        }
        return ms.ToArray();

        int Str(string s)
        {
            if (ids.TryGetValue(s, out var id)) return id;
            table.Add(s);
            return ids[s] = table.Count - 1;
        }
    }

    private static void WriteSteps(BinaryWriter w, IReadOnlyList<PendingReceiverStep> steps,
        List<string> table, Dictionary<string, int> ids)
    {
        w.Write7(steps.Count);
        foreach (var st in steps)
        {
            w.Write((byte)st.Kind);
            w.Write7(StrLocal(st.Name));
            w.Write7(st.Ns is null ? 0 : StrLocal(st.Ns));
            w.Write7(st.TypeSimpleName is null ? 0 : StrLocal(st.TypeSimpleName));
        }
        return;

        int StrLocal(string s)
        {
            if (ids.TryGetValue(s, out var id)) return id;
            table.Add(s);
            return ids[s] = table.Count - 1;
        }
    }

    // ------------------------------------------------------------- lectura

    public static List<FileFragment> Read(byte[] data, int expectedParserVersion)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        if (r.ReadBytes(4) is not { Length: 4 } magic || magic[0] != 'S' || magic[1] != 'G' || magic[2] != 'C' || magic[3] != '1')
            throw new InvalidDataException("Cache mágica inválida");
        var formatVersion = r.ReadInt32();
        if (formatVersion != FormatVersion)
            throw new InvalidDataException($"Formato de caché no soportado: v{formatVersion}");
        var parserVersion = r.ReadInt32();
        if (parserVersion != expectedParserVersion)
            throw new InvalidDataException($"ParserVersion distinto: {parserVersion} != {expectedParserVersion}");

        var fragmentCount = r.ReadInt32();
        var stringCount = r.Read7();
        var strings = new string[stringCount + 1];
        for (var i = 0; i < stringCount; i++)
        {
            var len = r.Read7();
            strings[i + 1] = Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        var fragments = new List<FileFragment>(fragmentCount);
        for (var i = 0; i < fragmentCount; i++)
        {
            var frag = new FileFragment
            {
                FilePath = r.S(strings),
                Hash = r.S(strings),
            };
            var nUsings = r.Read7();
            for (var u = 0; u < nUsings; u++) frag.Usings.Add(r.S(strings));
            var nAliases = r.Read7();
            for (var a = 0; a < nAliases; a++)
                frag.Aliases[r.S(strings)] = r.S(strings);

            var nNodes = r.Read7();
            for (var x = 0; x < nNodes; x++)
                frag.Nodes.Add(new NodeDef(
                    r.S(strings), r.S(strings), (NodeKind)r.ReadByte(),
                    r.ReadByte() == 1, r.Read7(), r.Read7(), r.S0(strings)));

            var nEdges = r.Read7();
            for (var x = 0; x < nEdges; x++)
                frag.Edges.Add(new TypeEdge(
                    r.S(strings), r.ReadByte() == 1, r.S(strings), r.ReadByte() == 1,
                    r.S(strings), (EdgeRelation)r.ReadByte(), r.Read7(), r.S0(strings)));

            var nEndpoints = r.Read7();
            for (var x = 0; x < nEndpoints; x++)
                frag.Endpoints.Add(new EndpointDef(
                    r.S(strings), r.S(strings), r.S(strings), r.S(strings), r.Read7()));

            var nCalls = r.Read7();
            for (var x = 0; x < nCalls; x++)
                frag.CallSites.Add(new CallSite(
                    r.S(strings), r.S(strings), r.S(strings), r.S(strings), r.S(strings), r.Read7()));

            var nDi = r.Read7();
            for (var x = 0; x < nDi; x++)
                frag.DiBindings.Add(new DiBinding(
                    r.S(strings), r.S(strings), r.S(strings), r.S(strings), r.Read7()));

            var nMembers = r.Read7();
            for (var x = 0; x < nMembers; x++)
                frag.Members.Add(new MemberSpan(
                    r.S(strings), r.S(strings), r.S(strings), r.S(strings),
                    r.Read7(), r.Read7(), r.ReadByte() == 1));

            var nRets = r.Read7();
            for (var x = 0; x < nRets; x++)
                frag.ReturnSignatures.Add(new MemberReturnSignature(
                    r.S(strings), r.S(strings), (MemberReturnKind)r.ReadByte(), r.S(strings), r.S(strings)));

            var nPending = r.Read7();
            for (var x = 0; x < nPending; x++)
            {
                var callerType = r.S(strings);
                var callerMember = r.S(strings);
                var calleeMember = r.S(strings);
                var receiver = ReadSteps(r, strings);
                var ns = r.S(strings);
                var line = r.Read7();
                frag.PendingCallSites.Add(new PendingCallSite(callerType, callerMember, calleeMember, receiver, ns, line));
            }

            var nLocals = r.Read7();
            for (var x = 0; x < nLocals; x++)
            {
                var declaringType = r.S(strings);
                var declaringMember = r.S(strings);
                var localName = r.S(strings);
                var initializer = ReadSteps(r, strings);
                var ns = r.S(strings);
                frag.PendingLocals.Add(new PendingLocal(declaringType, declaringMember, localName, initializer, ns));
            }

            fragments.Add(frag);
        }
        return fragments;
    }

    private static List<PendingReceiverStep> ReadSteps(BinaryReader r, string[] strings)
    {
        var count = r.Read7();
        var steps = new List<PendingReceiverStep>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = (PendingReceiverStepKind)r.ReadByte();
            var name = r.S(strings);
            var ns = r.S0(strings);
            var typeSimple = r.S0(strings);
            steps.Add(new PendingReceiverStep(kind, name, ns, typeSimple));
        }
        return steps;
    }
}

internal static class BinaryVarintExtensions
{
    /// <summary>varint LEB128 sin signo (suficiente: ids, líneas y counts son ≥ 0).</summary>
    public static void Write7(this BinaryWriter w, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            w.Write((byte)(v | 0x80));
            v >>= 7;
        }
        w.Write((byte)v);
    }

    public static int Read7(this BinaryReader r)
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 7)
        {
            var b = r.ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
        }
        return (int)result;
    }

    /// <summary>Cadena por índice de tabla (0 = null no ocurre aquí; strings[0] no se usa).</summary>
    public static string S(this BinaryReader r, string[] strings) => strings[r.Read7()];

    /// <summary>Cadena opcional: índice 0 = null.</summary>
    public static string? S0(this BinaryReader r, string[] strings)
    {
        var id = r.Read7();
        return id == 0 ? null : strings[id];
    }
}
