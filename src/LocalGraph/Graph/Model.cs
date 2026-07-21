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
/// Signatura de un miembro (método o propiedad) con su tipo de retorno/propiedad
/// en forma de nombre simple. Permite al grafo resolver receptores encadenados
/// (p.ej. <c>_factory.Get().RunAsync()</c>) conociendo que <c>IFactory.Get()</c>
/// devuelve <c>IService</c>. Sin esto, los call-sites encadenados se pierden.
/// </summary>
public sealed record MemberReturnSignature(
    string TypeName,         // FQN del tipo que declara el miembro
    string MemberName,       // nombre del método o propiedad
    MemberReturnKind Kind,   // method | property
    string ReturnSimpleType, // nombre simple del tipo de retorno/propiedad (a resolver)
    string Ns);

public enum MemberReturnKind { Method, Property }

/// <summary>
/// Un paso del receptor en un call-site encadenado pendiente de resolver.
/// El grafo recorre la secuencia para inferir el tipo del receptor final.
/// </summary>
public enum PendingReceiverStepKind { Local, MethodReturn, PropertyAccess }

public sealed record PendingReceiverStep(
    PendingReceiverStepKind Kind,
    string Name,             // local/field/param | method name | property name
    string? Ns,              // namespace de contexto si aplica
    string? TypeSimpleName); // para Local: nombre simple del tipo del local; null en otros kinds

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
    /// <summary>
    /// Signaturas de métodos/propiedades con su tipo de retorno. El grafo los usa
    /// para resolver receptores encadenados (<c>a.B().C()</c>) en RebuildLocked.
    /// </summary>
    public List<MemberReturnSignature> ReturnSignatures { get; init; } = [];
    /// <summary>
    /// Call-sites con receptor encadenado pendiente de resolver por el grafo.
    /// Cada uno lleva la secuencia de pasos del receptor (Local/Method/Property)
    /// que el grafo recorre para inferir el tipo del receptor final.
    /// </summary>
    public List<PendingCallSite> PendingCallSites { get; init; } = [];
    /// <summary>
    /// Locales de tipo <c>var x = expr;</c> cuyo tipo no se pudo inferir en el visitor
    /// (p.ej. <c>var x = await _factory.GetAsync();</c>). El grafo los resuelve en
    /// RebuildLocked y los registra en una tabla especial para que los call-sites
    /// que los usen como receptor se resuelvan también.
    /// </summary>
    public List<PendingLocal> PendingLocals { get; init; } = [];
}

/// <summary>
/// Un local declarado con <c>var</c> cuyo tipo no se pudo inferir durante el parseo.
/// Lleva la secuencia de pasos del inicializador para que el grafo la resuelva.
/// </summary>
public sealed record PendingLocal(
    string DeclaringType,      // FQN del tipo que contiene el método (o "&lt;top-level&gt;")
    string DeclaringMember,    // método que contiene la declaración
    string LocalName,          // nombre de la variable (svc, x, ...)
    IReadOnlyList<PendingReceiverStep> Initializer, // pasos del inicializador
    string Ns);

/// <summary>
/// Call-site cuyo receptor es una expresión encadenada (factory, member-access
/// profundo, indexer) que el visitor no puede resolver sin tabla de símbolos
/// global. El grafo lo resuelve en RebuildLocked caminando <see cref="Receiver"/>.
/// </summary>
public sealed record PendingCallSite(
    string CallerType,
    string CallerMember,
    string CalleeMember,
    IReadOnlyList<PendingReceiverStep> Receiver,
    string Ns,
    int Line);
