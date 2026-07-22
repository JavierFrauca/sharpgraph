using System.Xml.Linq;
using SharpGraph.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpGraph.Scanner;

/// <summary>
/// Recorre el AST de un fichero y construye un <see cref="FileFragment"/> aislado
/// (sin estado compartido, sin locks). El scanner luego fusiona los fragmentos.
/// </summary>
public sealed partial class TypeReferenceVisitor : CSharpSyntaxWalker
{
    private static readonly HashSet<string> HttpVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions" };

    // Genéricos contenedor / BCL que no aportan como nodos: ensucian el grafo
    // con cientos de callers falsos. Se filtran como destino de arista,
    // pero SÍ se desciende a sus argumentos de tipo.
    private static readonly HashSet<string> NoiseTypes = new(StringComparer.Ordinal)
    {
        "Task", "ValueTask", "Task`1", "List", "IList", "IEnumerable", "ICollection",
        "IReadOnlyList", "IReadOnlyCollection", "IAsyncEnumerable", "Dictionary",
        "IDictionary", "IReadOnlyDictionary", "HashSet", "ISet", "Queue", "Stack",
        "Func", "Action", "Predicate", "Tuple", "ValueTuple", "Span", "ReadOnlySpan",
        "Memory", "ReadOnlyMemory", "Nullable", "Lazy", "Array",
        "string", "String", "int", "Int32", "long", "Int64", "bool", "Boolean",
        "double", "decimal", "float", "byte", "char", "object", "Object", "void",
        "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "DateOnly", "TimeOnly",
        "CancellationToken"
    };

    // Interfaces de handler que crean una arista message -> handler.
    private static readonly HashSet<string> HandlerInterfaces = new(StringComparer.Ordinal)
    {
        "IRequestHandler", "INotificationHandler", "IConsumer", "ICommandHandler",
        "IQueryHandler", "IStreamRequestHandler", "IRequestPreProcessor", "IRequestPostProcessor"
    };

    // Métodos de envío de mensajes que crean una arista sender -> message.
    private static readonly HashSet<string> SendMethods = new(StringComparer.Ordinal)
    {
        "Send", "Publish", "Enqueue", "Schedule", "PublishAsync", "SendAsync", "Dispatch"
    };

    // Indicadores de tipo de receptor: si el receptor del Send/Publish resuelve a un tipo
    // cuyo nombre contiene alguno de estos fragmentos, se trata como bus de mensajes
    // (MediatR, MassTransit, NServiceBus, Rebus…). Si NO coincide, la invocación se
    // descarta como MediatR spurio (p.ej. smtp.Send(email), order.Dispatch()).
    // El matching es por subcadena sobre el nombre simple/cualificado del tipo resuelto.
    private static readonly HashSet<string> BusTypeHints = new(StringComparer.Ordinal)
    {
        "Mediator", "IMediator", "ISender", "IPublisher",
        "Bus", "IBus", "IPublishEndpoint", "ISendEndpoint",
        "MessageSession", "IMessageSession",
        "IBusControl", "IPublishEndpoint",
        "IBusAdvancedApi",
        "Dispatcher", "IDispatcher"
    };

    // Métodos de registro DI con 2 args de tipo: AddScoped<I,C>(), TryAddSingleton<I,C>()...
    private static readonly Dictionary<string, string> DiMethods = new(StringComparer.Ordinal)
    {
        ["AddScoped"] = "scoped", ["AddSingleton"] = "singleton", ["AddTransient"] = "transient",
        ["TryAddScoped"] = "scoped", ["TryAddSingleton"] = "singleton", ["TryAddTransient"] = "transient",
        ["AddKeyedScoped"] = "scoped", ["AddKeyedSingleton"] = "singleton", ["AddKeyedTransient"] = "transient",
    };

    private readonly FileFragment _fragment;

    private readonly Stack<string> _typeStack = new();
    private readonly Stack<bool> _typeVisibilityStack = new();
    private readonly Stack<string?> _routePrefixStack = new();
    // nombre de campo/propiedad/parámetro -> tipo declarado, para resolver invocaciones
    private readonly Stack<Dictionary<string, string>> _localsStack = new();

    private readonly Stack<bool> _memberVisibilityStack = new();
    private string? _currentMember;
    private readonly Stack<string> _namespaceStack = new();

    public TypeReferenceVisitor(FileFragment fragment) => _fragment = fragment;

    private string CurrentNs => _namespaceStack.Count > 0 ? _namespaceStack.Peek() : "";
    private string? CurrentType => _typeStack.Count > 0 ? _typeStack.Peek() : null;
    private bool CurrentTypeIsPublic => _typeVisibilityStack.Count > 0 && _typeVisibilityStack.Peek();
    private bool CurrentMemberIsPublic => _memberVisibilityStack.Count > 0 ? _memberVisibilityStack.Peek() : CurrentTypeIsPublic;
    private Dictionary<string, string>? CurrentLocals => _localsStack.Count > 0 ? _localsStack.Peek() : null;

    private int LineOf(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private (int Start, int End) SpanOf(SyntaxNode n)
    {
        var ls = n.GetLocation().GetLineSpan();
        return (ls.StartLinePosition.Line + 1, ls.EndLinePosition.Line + 1);
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var target = node.Name?.ToString();
        if (target is not null)
        {
            if (node.Alias is not null)
                _fragment.Aliases[node.Alias.Name.Identifier.Text] = target;
            else
                _fragment.Usings.Add(target);
        }
        base.VisitUsingDirective(node);
    }

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var ns = node.Name.ToString();
        _namespaceStack.Push(CurrentNs.Length == 0 ? ns : $"{CurrentNs}.{ns}");
        base.VisitNamespaceDeclaration(node);
        _namespaceStack.Pop();
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        _namespaceStack.Push(node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
        // file-scoped: no se hace pop (aplica al resto del fichero)
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeDecl(node.Identifier.Text, node.Modifiers, node.BaseList, node, NodeKind.Class, node.AttributeLists, () => base.VisitClassDeclaration(node));
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitTypeDecl(node.Identifier.Text, node.Modifiers, node.BaseList, node, NodeKind.Interface, node.AttributeLists, () => base.VisitInterfaceDeclaration(node));
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        => VisitTypeDecl(node.Identifier.Text, node.Modifiers, node.BaseList, node, NodeKind.Record, node.AttributeLists, () => base.VisitRecordDeclaration(node));
    public override void VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeDecl(node.Identifier.Text, node.Modifiers, node.BaseList, node, NodeKind.Struct, node.AttributeLists, () => base.VisitStructDeclaration(node));

    private void VisitTypeDecl(string simpleName, SyntaxTokenList modifiers, BaseListSyntax? baseList,
        SyntaxNode node, NodeKind kind, SyntaxList<AttributeListSyntax> attrs, Action visitBase)
    {
        // FQN = namespace + cadena de tipos anidados:
        //   Ventas.CreateUserCommand.Handler  (mata colisiones entre namespaces)
        var ns = CurrentNs;
        var name = _typeStack.Count > 0
            ? $"{_typeStack.Peek()}.{simpleName}"
            : ns.Length == 0 ? simpleName : $"{ns}.{simpleName}";
        var isPublicType = IsPublic(modifiers);
        var summary = ExtractSummaryXml(node);
        var (start, end) = SpanOf(node);

        _fragment.Nodes.Add(new NodeDef(name, ns, kind, isPublicType, start, end, summary));

        // Prefijo de ruta a nivel de clase: [Route("api/[controller]")]
        var routePrefix = ExtractControllerRoute(attrs, simpleName);

        _typeStack.Push(name);
        _typeVisibilityStack.Push(isPublicType);
        _routePrefixStack.Push(routePrefix);
        _localsStack.Push(new Dictionary<string, string>(StringComparer.Ordinal));

        ProcessBaseList(baseList, isPublicType, name);
        // Constructor primario de records: record Foo(IBar bar)
        ProcessPrimaryConstructor(node);
        visitBase();

        _localsStack.Pop();
        _routePrefixStack.Pop();
        _typeVisibilityStack.Pop();
        _typeStack.Pop();
    }

    private void ProcessPrimaryConstructor(SyntaxNode node)
    {
        var paramList = node switch
        {
            RecordDeclarationSyntax r => r.ParameterList,
            ClassDeclarationSyntax c => c.ParameterList,
            StructDeclarationSyntax s => s.ParameterList,
            _ => null
        };
        if (paramList is null) return;
        foreach (var p in paramList.Parameters)
            if (p.Type is not null)
                AddRefs(p.Type, EdgeRelation.CtorParam, CurrentTypeIsPublic, LineOf(p), registerLocal: p.Identifier.Text);
    }

    private void RegisterLambdaParams(ExpressionSyntax expr)
    {
        var paramList = expr switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
            _ => default
        };
        foreach (var p in paramList)
            if (p.Type is not null) RegisterLocal(p.Identifier.Text, p.Type);
    }
}
