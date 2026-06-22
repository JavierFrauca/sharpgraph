namespace LocalGraph.Graph;

/// <summary>
/// Tipo de relación entre dos tipos. Antes se perdía (el grafo solo guardaba
/// nombres en un HashSet); ahora viaja en cada arista para dar precisión sin
/// apenas coste de tokens en la salida.
/// </summary>
public enum EdgeRelation
{
    Inherits,     // : BaseClass
    Implements,   // : IInterface
    CtorParam,    // dependencia inyectada por constructor
    FieldType,    // campo de ese tipo
    PropertyType, // propiedad de ese tipo
    New,          // new T() / new()
    Call,         // invocación real de un miembro (a nivel de método)
    Sends,        // _mediator.Send(new XCommand()) / Publish
    HandledBy,    // XCommand -> XCommand.Handler  (MediatR/MassTransit)
    DiBound,      // IFoo -> Foo  (AddScoped<IFoo,Foo>)
    ReturnType,   // tipo de retorno de un método público
    ParamType,    // parámetro de un método
}

public static class EdgeRelationExtensions
{
    public static string Label(this EdgeRelation r) => r switch
    {
        EdgeRelation.Inherits => "inherits",
        EdgeRelation.Implements => "implements",
        EdgeRelation.CtorParam => "ctor-param",
        EdgeRelation.FieldType => "field",
        EdgeRelation.PropertyType => "property",
        EdgeRelation.New => "new",
        EdgeRelation.Call => "call",
        EdgeRelation.Sends => "sends",
        EdgeRelation.HandledBy => "handled-by",
        EdgeRelation.DiBound => "di-bound",
        EdgeRelation.ReturnType => "returns",
        EdgeRelation.ParamType => "param",
        _ => "uses"
    };
}

public enum NodeKind { Class, Interface, Record, Struct, Enum, Unknown }

/// <summary>
/// Una arista tipo→tipo. Los extremos pueden estar ya resueltos (FQN del tipo
/// declarado, <c>*Resolved=true</c>) o ser nombres simples a resolver en el rebuild
/// usando <see cref="Ns"/> + los usings del fichero. Esto mantiene la resolución
/// de identidad consistente bajo escaneo incremental.
/// </summary>
public sealed record TypeEdge(
    string From,
    bool FromResolved,
    string To,
    bool ToResolved,
    string Ns,
    EdgeRelation Relation,
    int Line,
    string? FromMember = null);

/// <summary>Un tipo declarado en la solución. <see cref="Name"/> es el FQN.</summary>
public sealed record NodeDef(
    string Name,
    string Namespace,
    NodeKind Kind,
    bool IsPublic,
    int StartLine,
    int EndLine,
    string? Summary);

/// <summary>Un miembro (método/ctor/prop) con su span, para get_source a nivel fino.</summary>
public sealed record MemberSpan(
    string TypeName,
    string MemberName,
    string Kind,        // method | ctor | property | field
    string Signature,
    int StartLine,
    int EndLine,
    bool IsPublic);

public sealed record EndpointDef(
    string TypeName,
    string Verb,
    string Route,
    string MethodName,
    int Line);

/// <summary>
/// Un sitio concreto donde se invoca un miembro de otro tipo. <see cref="CallerType"/>
/// es FQN (tipo declarado); <see cref="CalleeType"/> es un nombre simple a resolver
/// en <see cref="Ns"/>.
/// </summary>
public sealed record CallSite(
    string CallerType,
    string CallerMember,
    string CalleeType,
    string CalleeMember,
    string Ns,
    int Line);

/// <summary>Un binding de inyección de dependencias: IFoo -> Foo (nombres a resolver en Ns).</summary>
public sealed record DiBinding(
    string ServiceType,
    string ImplementationType,
    string Lifetime,   // scoped | singleton | transient
    string Ns,
    int Line);

/// <summary>
/// Todo lo que se extrae de UN fichero .cs. La unidad de incrementalidad:
/// al cambiar un fichero se reemplaza su fragmento y se reconstruye el índice.
/// </summary>
public sealed class FileFragment
{
    public required string FilePath { get; init; }
    public required string Hash { get; init; }
    /// <summary>Namespaces importados por el fichero (using X;).</summary>
    public List<string> Usings { get; init; } = [];
    /// <summary>Alias de using: alias -> nombre destino (using A = Some.Type;).</summary>
    public Dictionary<string, string> Aliases { get; init; } = new(StringComparer.Ordinal);
    public List<NodeDef> Nodes { get; init; } = [];
    public List<TypeEdge> Edges { get; init; } = [];
    public List<EndpointDef> Endpoints { get; init; } = [];
    public List<CallSite> CallSites { get; init; } = [];
    public List<DiBinding> DiBindings { get; init; } = [];
    public List<MemberSpan> Members { get; init; } = [];
}
