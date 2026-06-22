using System.Xml.Linq;
using LocalGraph.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalGraph.Scanner;

/// <summary>
/// Recorre el AST de un fichero y construye un <see cref="FileFragment"/> aislado
/// (sin estado compartido, sin locks). El scanner luego fusiona los fragmentos.
/// </summary>
public sealed class TypeReferenceVisitor : CSharpSyntaxWalker
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

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (CurrentType is null) { base.VisitConstructorDeclaration(node); return; }

        var isPublic = IsPublic(node.Modifiers);
        _currentMember = ".ctor";
        var (start, end) = SpanOf(node);
        _fragment.Members.Add(new MemberSpan(CurrentType, ".ctor", "ctor",
            BuildCtorSignature(node), start, end, isPublic));

        foreach (var p in node.ParameterList.Parameters)
            if (p.Type is not null)
                AddRefs(p.Type, EdgeRelation.CtorParam, isPublic, LineOf(p), registerLocal: p.Identifier.Text);

        _memberVisibilityStack.Push(isPublic);
        base.VisitConstructorDeclaration(node);
        _memberVisibilityStack.Pop();
        _currentMember = null;
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (CurrentType is null) { base.VisitFieldDeclaration(node); return; }
        var line = LineOf(node);
        foreach (var v in node.Declaration.Variables)
            RegisterLocal(v.Identifier.Text, node.Declaration.Type);
        AddRefs(node.Declaration.Type, EdgeRelation.FieldType, IsPublic(node.Modifiers), line);
        base.VisitFieldDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (CurrentType is null) { base.VisitPropertyDeclaration(node); return; }
        RegisterLocal(node.Identifier.Text, node.Type);
        AddRefs(node.Type, EdgeRelation.PropertyType, IsPublic(node.Modifiers), LineOf(node));
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (CurrentType is null) { base.VisitMethodDeclaration(node); return; }

        var isPublic = IsPublic(node.Modifiers);
        _currentMember = node.Identifier.Text;
        _memberVisibilityStack.Push(isPublic);

        var (start, end) = SpanOf(node);
        _fragment.Members.Add(new MemberSpan(CurrentType, node.Identifier.Text, "method",
            BuildMethodSignature(node), start, end, isPublic));

        DetectEndpoint(node);

        // tipos públicos de retorno y parámetros: útiles para entender contratos
        if (isPublic)
        {
            AddRefs(node.ReturnType, EdgeRelation.ReturnType, true, LineOf(node));
            foreach (var p in node.ParameterList.Parameters)
                if (p.Type is not null)
                {
                    AddRefs(p.Type, EdgeRelation.ParamType, true, LineOf(p), registerLocal: p.Identifier.Text);
                }
        }
        else
        {
            foreach (var p in node.ParameterList.Parameters)
                if (p.Type is not null) RegisterLocal(p.Identifier.Text, p.Type);
        }

        base.VisitMethodDeclaration(node);
        _memberVisibilityStack.Pop();
        _currentMember = null;
    }

    // Métodos genéricos que NO devuelven T sino una colección de T: no inferir el tipo.
    private static readonly HashSet<string> GenericCollectionMethods = new(StringComparer.Ordinal)
        { "OfType", "Cast" };

    public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
    {
        if (node.Type is not null && !node.Type.IsVar)
        {
            // tipado explícito: PaymentService svc = ...;
            foreach (var v in node.Variables)
                RegisterLocal(v.Identifier.Text, node.Type);
        }
        else
        {
            // var: inferir el tipo desde el inicializador (new T(), factorías DI Get<T>(), cast).
            foreach (var v in node.Variables)
            {
                var inferred = InferInitializerType(v.Initializer?.Value);
                if (inferred is not null && IsMeaningful(inferred) && CurrentLocals is not null)
                    CurrentLocals[v.Identifier.Text] = inferred;
            }
        }
        base.VisitVariableDeclaration(node);
    }

    /// <summary>
    /// Infiere el nombre de tipo de un inicializador de variable, best-effort sin modelo semántico:
    ///   new Foo()                                  -> Foo
    ///   provider.GetRequiredService&lt;Foo&gt;()   -> Foo  (locator/factoría genérica)
    ///   Resolve&lt;Foo&gt;() / Deserialize&lt;Foo&gt;() -> Foo
    ///   (Foo)expr                                  -> Foo
    /// </summary>
    private static string? InferInitializerType(ExpressionSyntax? init) => init switch
    {
        ObjectCreationExpressionSyntax oc => ExtractNames(oc.Type).FirstOrDefault(),
        CastExpressionSyntax cast => ExtractNames(cast.Type).FirstOrDefault(),
        AwaitExpressionSyntax aw => InferInitializerType(aw.Expression),
        InvocationExpressionSyntax inv => InferFromGenericCall(inv),
        _ => null
    };

    private static string? InferFromGenericCall(InvocationExpressionSyntax inv)
    {
        var gen = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name as GenericNameSyntax,        // x.Get<T>()
            GenericNameSyntax g => g,                                               // Get<T>()
            _ => null
        };
        if (gen is null || GenericCollectionMethods.Contains(gen.Identifier.Text)) return null;
        var args = gen.TypeArgumentList.Arguments;
        return args.Count >= 1 ? ExtractNames(args[0]).FirstOrDefault() : null;
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (CurrentType is not null)
            AddRefs(node.Type, EdgeRelation.New, CurrentMemberIsPublic, LineOf(node));
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        // new(...) — intentamos resolver el tipo objetivo desde el contexto
        // (asignación a campo/var de tipo conocido). Best-effort.
        var target = ResolveImplicitNewTarget(node);
        if (CurrentType is not null && target is not null && IsMeaningful(target))
            AddEdge(target, EdgeRelation.New, LineOf(node), CurrentMemberIsPublic);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // DI se registra a menudo en top-level statements (sin tipo): detectar siempre.
        DetectDiBinding(node);
        // Minimal API: app.MapGet("/x", handler) — crea un endpoint sintético y
        // procesa el cuerpo del handler bajo ese contexto (para que las llamadas
        // a servicios dentro del lambda se atribuyan al endpoint y la traza funcione).
        if (TryHandleMinimalApi(node)) return;
        if (CurrentType is not null)
        {
            DetectSend(node);
            DetectCall(node);
        }
        base.VisitInvocationExpression(node);
    }

    private static readonly Dictionary<string, string> MinimalApiMaps = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    private bool TryHandleMinimalApi(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax ma) return false;
        if (!MinimalApiMaps.TryGetValue(ma.Name.Identifier.Text, out var verb)) return false;

        var args = node.ArgumentList.Arguments;
        if (args.Count < 1) return false;
        var route = args[0].Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
        if (route is null) return false;

        var owner = $"{verb} {route}";  // nodo sintético, NO se añade a _nodes (search limpio)
        _fragment.Endpoints.Add(new EndpointDef(owner, verb, route, "(minimal-api)", LineOf(node)));

        // método-grupo: app.MapGet("/x", SomeType.Handle) → arista owner -> SomeType
        if (args.Count >= 2 && args[1].Expression is MemberAccessExpressionSyntax handlerRef
            && handlerRef.Expression is IdentifierNameSyntax declType && IsMeaningful(declType.Identifier.Text))
            _fragment.Edges.Add(new TypeEdge(owner, true, declType.Identifier.Text, false, CurrentNs, EdgeRelation.Call, LineOf(node)));

        // procesar el cuerpo del handler bajo el contexto del endpoint sintético
        _typeStack.Push(owner);
        _typeVisibilityStack.Push(true);
        _routePrefixStack.Push(null);
        _localsStack.Push(new Dictionary<string, string>(StringComparer.Ordinal));
        var prevMember = _currentMember;
        _currentMember = "(handler)";

        // registrar parámetros tipados del lambda: (IService svc, int id) => ...
        foreach (var arg in args.Skip(1))
            RegisterLambdaParams(arg.Expression);

        base.VisitInvocationExpression(node);

        _currentMember = prevMember;
        _localsStack.Pop();
        _routePrefixStack.Pop();
        _typeVisibilityStack.Pop();
        _typeStack.Pop();
        return true;
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

    // ---- Detección de llamadas reales (matatokens: "¿dónde se invoca X?") ----

    private void DetectCall(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax ma) return;
        var method = ma.Name.Identifier.Text;

        // receptor: _grossService.Calculate()  ó  this.foo.Calculate()
        var receiver = ma.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax inner when inner.Expression is ThisExpressionSyntax
                => inner.Name.Identifier.Text,
            _ => null
        };
        if (receiver is null) return;

        var calleeType = LookupLocal(receiver);
        if (calleeType is null || !IsMeaningful(calleeType)) return;

        _fragment.CallSites.Add(new CallSite(
            CurrentType!, _currentMember ?? "?", calleeType, method, CurrentNs, LineOf(node)));
        AddEdge(calleeType, EdgeRelation.Call, LineOf(node), CurrentMemberIsPublic, _currentMember);
    }

    // ---- Detección MediatR / bus: _mediator.Send(new XCommand()) ----

    private void DetectSend(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax ma) return;
        if (!SendMethods.Contains(ma.Name.Identifier.Text)) return;

        var firstArg = node.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        string? messageType = firstArg switch
        {
            ObjectCreationExpressionSyntax oc => ExtractNames(oc.Type).FirstOrDefault(),
            ImplicitObjectCreationExpressionSyntax => null,
            IdentifierNameSyntax id => LookupLocal(id.Identifier.Text),
            _ => null
        };
        if (messageType is null || !IsMeaningful(messageType)) return;

        AddEdge(messageType, EdgeRelation.Sends, LineOf(node), CurrentMemberIsPublic, _currentMember);
    }

    // ---- Detección DI: services.AddScoped<IFoo, Foo>() ----

    private void DetectDiBinding(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax ma) return;
        if (ma.Name is not GenericNameSyntax g) { DetectDiBindingTypeof(node, ma); return; }
        if (!DiMethods.TryGetValue(g.Identifier.Text, out var lifetime)) return;

        var args = g.TypeArgumentList.Arguments;
        if (args.Count < 2) return; // AddScoped<C>() (auto-binding) lo ignoramos

        var service = ExtractNames(args[0]).FirstOrDefault();
        var impl = ExtractNames(args[1]).FirstOrDefault();
        if (service is null || impl is null || service == impl) return;

        _fragment.DiBindings.Add(new DiBinding(service, impl, lifetime, CurrentNs, LineOf(node)));
        // arista dirigida servicio -> implementación (ambos extremos a resolver)
        _fragment.Edges.Add(new TypeEdge(service, false, impl, false, CurrentNs, EdgeRelation.DiBound, LineOf(node)));
    }

    private void DetectDiBindingTypeof(InvocationExpressionSyntax node, MemberAccessExpressionSyntax ma)
    {
        if (!DiMethods.TryGetValue(ma.Name.Identifier.Text, out var lifetime)) return;
        var typeofArgs = node.ArgumentList.Arguments
            .Select(a => a.Expression).OfType<TypeOfExpressionSyntax>()
            .Select(t => ExtractNames(t.Type).FirstOrDefault())
            .Where(n => n is not null).ToList();
        if (typeofArgs.Count < 2) return;
        var service = typeofArgs[0]!;
        var impl = typeofArgs[1]!;
        if (service == impl) return;
        _fragment.DiBindings.Add(new DiBinding(service, impl, lifetime, CurrentNs, LineOf(node)));
        _fragment.Edges.Add(new TypeEdge(service, false, impl, false, CurrentNs, EdgeRelation.DiBound, LineOf(node)));
    }

    private void DetectEndpoint(MethodDeclarationSyntax node)
    {
        foreach (var attrList in node.AttributeLists)
        foreach (var attr in attrList.Attributes)
        {
            var attrName = attr.Name.ToString();
            var simpleName = (attrName.Contains('.') ? attrName.Split('.').Last() : attrName)
                .Replace("Attribute", "");

            if (!HttpVerbs.Contains(simpleName)) continue;

            var verb = simpleName.Replace("Http", "").ToUpperInvariant();
            var actionRoute = ExtractRouteArg(attr);
            var route = CombineRoute(_routePrefixStack.Count > 0 ? _routePrefixStack.Peek() : null, actionRoute, node.Identifier.Text);
            _fragment.Endpoints.Add(new EndpointDef(CurrentType!, verb, route, node.Identifier.Text, LineOf(node)));
        }
    }

    // ---------- helpers de edges ----------

    private void AddRefs(TypeSyntax type, EdgeRelation relation, bool isPublic, int line, string? registerLocal = null)
    {
        if (CurrentType is null) return;
        if (registerLocal is not null) RegisterLocal(registerLocal, type);
        foreach (var name in ExtractNames(type))
            AddEdge(name, relation, line, isPublic);
    }

    private void AddEdge(string to, EdgeRelation relation, int line, bool isPublic, string? fromMember = null)
    {
        if (CurrentType is null || !IsMeaningful(to) || string.IsNullOrWhiteSpace(to)) return;
        // From = tipo declarado actual (FQN, ya resuelto); To = referencia simple (a resolver).
        _fragment.Edges.Add(new TypeEdge(CurrentType, true, to, false, CurrentNs, relation, line, fromMember));
    }

    private void ProcessBaseList(BaseListSyntax? baseList, bool isPublicType, string typeName)
    {
        if (baseList is null) return;
        foreach (var t in baseList.Types)
        {
            // ¿es una interfaz de handler? message -> handler (HandledBy)
            if (t.Type is GenericNameSyntax g && HandlerInterfaces.Contains(g.Identifier.Text))
            {
                var message = g.TypeArgumentList.Arguments
                    .Select(a => ExtractNames(a).FirstOrDefault())
                    .FirstOrDefault(n => n is not null && IsMeaningful(n));
                if (message is not null)
                    // message (a resolver) -> handler (FQN ya resuelto)
                    _fragment.Edges.Add(new TypeEdge(message, false, typeName, true, CurrentNs, EdgeRelation.HandledBy, LineOf(t)));
            }

            foreach (var name in ExtractNames(t.Type))
            {
                if (!IsMeaningful(name)) continue;
                var rel = LooksLikeInterface(name) ? EdgeRelation.Implements : EdgeRelation.Inherits;
                _fragment.Edges.Add(new TypeEdge(typeName, true, name, false, CurrentNs, rel, LineOf(t)));
            }
        }
    }

    private static bool LooksLikeInterface(string name)
    {
        var simple = name.Contains('.') ? name.Split('.').Last() : name;
        return simple.Length >= 2 && simple[0] == 'I' && char.IsUpper(simple[1]);
    }

    // ---------- locals (resolución de invocaciones) ----------

    private void RegisterLocal(string name, TypeSyntax type)
    {
        var locals = CurrentLocals;
        if (locals is null) return;
        var typeName = ExtractNames(type).FirstOrDefault();
        if (typeName is not null) locals[name] = typeName;
    }

    private string? LookupLocal(string name)
        => CurrentLocals is not null && CurrentLocals.TryGetValue(name, out var t) ? t : null;

    private string? ResolveImplicitNewTarget(ImplicitObjectCreationExpressionSyntax node)
    {
        // asignación: _field = new(...);  ó  Prop = new(...);
        if (node.Parent is AssignmentExpressionSyntax asg)
        {
            var lhs = asg.Left switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            };
            if (lhs is not null) return LookupLocal(lhs);
        }
        // declaración: PaymentService svc = new(...);
        if (node.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax }
            && node.Parent.Parent?.Parent is VariableDeclarationSyntax { Type: var vt } && !vt.IsVar)
            return ExtractNames(vt).FirstOrDefault();
        return null;
    }

    // ---------- rutas de endpoint ----------

    private static string? ExtractControllerRoute(SyntaxList<AttributeListSyntax> attrs, string typeSimpleName)
    {
        foreach (var list in attrs)
        foreach (var attr in list.Attributes)
        {
            var n = attr.Name.ToString();
            var simple = n.Contains('.') ? n.Split('.').Last() : n;
            if (simple is not ("Route" or "RouteAttribute")) continue;
            var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
            if (arg?.Expression is LiteralExpressionSyntax lit)
                return SubstituteControllerToken(lit.Token.ValueText, typeSimpleName);
        }
        return null;
    }

    private static string SubstituteControllerToken(string route, string typeSimpleName)
    {
        var controllerName = typeSimpleName.EndsWith("Controller", StringComparison.Ordinal)
            ? typeSimpleName[..^"Controller".Length]
            : typeSimpleName;
        return route.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineRoute(string? prefix, string? actionRoute, string fallbackMethodName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefix)) parts.Add(prefix.Trim('/'));
        if (!string.IsNullOrWhiteSpace(actionRoute)) parts.Add(actionRoute.Trim('/'));
        if (parts.Count == 0) return actionRoute ?? fallbackMethodName;
        return "/" + string.Join("/", parts);
    }

    private static string? ExtractRouteArg(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault();
        return arg?.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null;
    }

    // ---------- nombres / tipos ----------

    private static bool IsMeaningful(string name) => !string.IsNullOrWhiteSpace(name) && !NoiseTypes.Contains(name);

    private static IEnumerable<string> ExtractNames(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => [id.Identifier.Text],
        // genérico: NO añadimos el contenedor (Task/List...) si es ruido,
        // pero siempre descendemos a los argumentos de tipo.
        GenericNameSyntax g => NoiseTypes.Contains(g.Identifier.Text)
            ? g.TypeArgumentList.Arguments.SelectMany(ExtractNames)
            : [g.Identifier.Text, .. g.TypeArgumentList.Arguments.SelectMany(ExtractNames)],
        // tipo cualificado: nos quedamos con el nombre simple (sin namespace),
        // que es como se indexa todo el grafo.
        QualifiedNameSyntax q => ExtractNames(q.Right),
        AliasQualifiedNameSyntax a => ExtractNames(a.Name),
        NullableTypeSyntax n => ExtractNames(n.ElementType),
        ArrayTypeSyntax a => ExtractNames(a.ElementType),
        TupleTypeSyntax t => t.Elements.SelectMany(e => ExtractNames(e.Type)),
        _ => []
    };

    private static string? ExtractSummaryXml(SyntaxNode node)
    {
        foreach (var t in node.GetLeadingTrivia())
        {
            if (!t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                !t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;
            try
            {
                var doc = XDocument.Parse("<root>" + t.ToFullString() + "</root>");
                var summary = doc.Descendants("summary").FirstOrDefault();
                if (summary != null)
                {
                    // Roslyn incluye los marcadores '///' en el texto de la trivia: quitarlos.
                    var text = string.Join(" ", summary.Value.Split('\n', '\r')
                        .Select(s => s.Trim().TrimStart('/').Trim()).Where(s => s.Length > 0));
                    if (text.Length > 0) return text;
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));

    // ---------- firmas (para get_source compacto) ----------

    private static string BuildMethodSignature(MethodDeclarationSyntax node)
    {
        var mods = string.Join(" ", node.Modifiers.Select(m => m.Text));
        var ret = node.ReturnType.ToString();
        var name = node.Identifier.Text + node.TypeParameterList?.ToString();
        var pars = node.ParameterList.ToString();
        return $"{mods} {ret} {name}{pars}".Trim();
    }

    private static string BuildCtorSignature(ConstructorDeclarationSyntax node)
    {
        var mods = string.Join(" ", node.Modifiers.Select(m => m.Text));
        return $"{mods} {node.Identifier.Text}{node.ParameterList}".Trim();
    }
}
