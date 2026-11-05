using System.Runtime.CompilerServices;
using System.Text;

namespace OneLlm.Templating;

/// <summary>One render in progress: the output buffer, variables and embed stack. Not thread-safe; each render makes its own.</summary>
internal sealed class Exec
{
    private enum Flow { Normal, Break, Continue }

    private struct Var
    {
        public string Name;
        public object? Value;

        // True for a range variable over a list or map: its value is still an interface value, which `slice` does not unwrap.
        public bool Iface;
    }

    private const int MaxTemplateCallDepth = 100_000;

    private readonly TemplateRenderOptions _opts;
    private readonly StringBuilder _sb;
    private readonly List<string> _embedStack;
    private readonly object? _root;
    private ParsedTemplate _tmpl;
    private List<Var> _vars = new();
    private int _callDepth;
    private bool _dotIface;
    private Node? _callNode;

    public Exec(ParsedTemplate tmpl, object? root, StringBuilder sb, TemplateRenderOptions opts, List<string> embedStack)
    {
        _tmpl = tmpl;
        // Nil data is no value at all in the reference: field access on it yields no value instead of failing.
        object? normRoot = V.Norm(root);
        _root = normRoot ?? V.Missing;
        _sb = sb;
        _opts = opts;
        _embedStack = embedStack;
    }

    public TimeProvider? Clock => _opts.TimeProvider;

    public void Run()
    {
        _vars.Add(new Var { Name = "$", Value = _root });
        try
        {
            WalkList(_tmpl.Root, _root);
        }
        catch (InsufficientExecutionStackException)
        {
            throw new TemplateException(TemplateErrorKind.Limit, _tmpl.FileName ?? _tmpl.Name, 0, 0, -1, "template is nested too deeply to render");
        }
    }

    private TemplateException Err(Node n, string message) => _tmpl.Error(TemplateErrorKind.Execute, n.Pos, message);

    // ---- walking ----

    private Flow WalkList(ListNode list, object? dot)
    {
        foreach (Node n in list.Nodes)
        {
            Flow f = Walk(n, dot);
            if (f != Flow.Normal)
            {
                return f;
            }
        }

        return Flow.Normal;
    }

    private Flow Walk(Node n, object? dot)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (n)
        {
            case TextNode t:
                Append(t.Text, n);
                return Flow.Normal;
            case ActionNode a:
            {
                _opts.CancellationToken.ThrowIfCancellationRequested();
                object? v = EvalPipeline(dot, a.Pipe);
                if (a.Pipe.Decl.Count == 0)
                {
                    Print(v, n);
                }

                return Flow.Normal;
            }

            case BranchNode b:
                return WalkBranch(b, dot);
            case TemplateCallNode tc:
                WalkTemplate(tc, dot);
                return Flow.Normal;
            case BreakNode:
                return Flow.Break;
            case ContinueNode:
                return Flow.Continue;
            default:
                throw Err(n, "unknown node");
        }
    }

    private Flow WalkBranch(BranchNode b, object? dot)
    {
        int mark = _vars.Count;
        try
        {
            if (b.Kind == BranchKind.Range)
            {
                return WalkRange(b, dot, mark);
            }

            object? val = EvalPipeline(dot, b.Pipe);
            if (V.IsTrue(val))
            {
                if (b.Kind != BranchKind.With)
                {
                    return WalkList(b.List, dot);
                }

                bool savedIface = _dotIface;
                _dotIface = false;
                try
                {
                    return WalkList(b.List, val);
                }
                finally
                {
                    _dotIface = savedIface;
                }
            }

            if (b.ElseList is not null)
            {
                return WalkList(b.ElseList, dot);
            }

            return Flow.Normal;
        }
        finally
        {
            _vars.RemoveRange(mark, _vars.Count - mark);
        }
    }

    private Flow WalkRange(BranchNode b, object? dot, int mark)
    {
        PipeNode pipe = b.Pipe;
        object? val = EvalCommands(dot, pipe);
        if (!pipe.IsAssign)
        {
            foreach (VariableNode d in pipe.Decl)
            {
                // Until an iteration sets them, the variables hold the whole range operand (observed: an empty range's
                // else branch sees it).
                _vars.Add(new Var { Name = d.Name, Value = val });
            }
        }
        else
        {
            foreach (VariableNode d in pipe.Decl)
            {
                if (FindVar(d.Name) < 0)
                {
                    throw Err(d, $"undefined variable: {d.Name}");
                }
            }
        }

        if ((val is long || val is byte) && pipe.Decl.Count > 1)
        {
            throw Err(pipe, $"can't use {val} to iterate over more than one variable");
        }

        bool any = false;
        Flow result = Flow.Normal;
        bool ifaceElems = V.IsList(val) || V.IsMap(val);

        bool Iteration(object? key, object? elem)
        {
            _opts.CancellationToken.ThrowIfCancellationRequested();
            any = true;
            if (pipe.Decl.Count == 1)
            {
                SetRangeVar(pipe.Decl[0].Name, elem, ifaceElems);
            }
            else if (pipe.Decl.Count == 2)
            {
                SetRangeVar(pipe.Decl[0].Name, key, false);
                SetRangeVar(pipe.Decl[1].Name, elem, ifaceElems);
            }

            int inner = _vars.Count;
            bool savedIface = _dotIface;
            _dotIface = ifaceElems;
            Flow f;
            try
            {
                f = WalkList(b.List, elem);
            }
            finally
            {
                _dotIface = savedIface;
            }

            _vars.RemoveRange(inner, _vars.Count - inner);
            return f != Flow.Break;
        }

        if (val is null || ReferenceEquals(val, V.Missing))
        {
            // nothing to iterate
        }
        else if (V.IsList(val))
        {
            int n = V.ListCount(val);
            for (int i = 0; i < n; i++)
            {
                if (!Iteration((long)i, V.ListGet(val, i)))
                {
                    break;
                }
            }
        }
        else if (V.IsMap(val))
        {
            foreach (string k in V.MapKeys(val))
            {
                V.MapTryGet(val, k, out object? e);
                if (!Iteration(k, e))
                {
                    break;
                }
            }
        }
        else if (val is long or byte)
        {
            long count = val is long lv ? lv : (byte)val;
            // Range over an integer yields 0 .. n-1.
            for (long i = 0; i < count; i++)
            {
                if (!Iteration(i, i))
                {
                    break;
                }
            }
        }
        else
        {
            throw Err(pipe, $"range can't iterate over {GoFmt.Sprint(new[] { val })}");
        }

        if (!any && b.ElseList is not null)
        {
            result = WalkList(b.ElseList, dot);
        }

        _ = mark;
        return result == Flow.Normal ? Flow.Normal : result;
    }

    private void SetRangeVar(string name, object? value, bool iface)
    {
        int i = FindVar(name);
        _vars[i] = new Var { Name = name, Value = value, Iface = iface };
    }

    private void WalkTemplate(TemplateCallNode tc, object? dot)
    {
        if (!_tmpl.Defs.TryGetValue(tc.Name, out ListNode? body))
        {
            if (tc.Name == _tmpl.Name)
            {
                body = _tmpl.Root;
            }
            else
            {
                throw Err(tc, $"template \"{tc.Name}\" not defined");
            }
        }

        if (_callDepth >= MaxTemplateCallDepth)
        {
            throw new TemplateException(TemplateErrorKind.Limit, _tmpl.FileName ?? _tmpl.Name, 0, 0, -1, "exceeded maximum template depth");
        }

        // No pipeline means the called template starts with no value at all (not even nil), as in the reference.
        object? arg = tc.Pipe is null ? V.Missing : EvalPipeline(dot, tc.Pipe);
        List<Var> saved = _vars;
        bool savedIface = _dotIface;
        _vars = new List<Var> { new() { Name = "$", Value = arg } };
        _dotIface = false;
        _callDepth++;
        try
        {
            WalkList(body, arg);
        }
        finally
        {
            _callDepth--;
            _vars = saved;
            _dotIface = savedIface;
        }
    }

    // ---- output ----

    private void Append(string s, Node at)
    {
        if (_sb.Length + s.Length > _opts.MaxOutputLength)
        {
            throw _tmpl.Error(TemplateErrorKind.Limit, at.Pos, "output is longer than the allowed maximum");
        }

        _sb.Append(s);
    }

    private void Print(object? v, Node at)
    {
        v = V.Norm(v);
        if (v is null || ReferenceEquals(v, V.Missing))
        {
            Append("<no value>", at);
            return;
        }

        if (v is string s)
        {
            Append(s, at);
            return;
        }

        int before = _sb.Length;
        GoFmt.AppendV(_sb, v);
        if (_sb.Length > _opts.MaxOutputLength)
        {
            _sb.Length = before;
            throw _tmpl.Error(TemplateErrorKind.Limit, at.Pos, "output is longer than the allowed maximum");
        }
    }

    // ---- variables ----

    private int FindVar(string name)
    {
        for (int i = _vars.Count - 1; i >= 0; i--)
        {
            if (_vars[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- pipelines ----

    private object? EvalPipeline(object? dot, PipeNode pipe)
    {
        object? value = EvalCommands(dot, pipe);
        foreach (VariableNode d in pipe.Decl)
        {
            if (pipe.IsAssign)
            {
                int i = FindVar(d.Name);
                if (i < 0)
                {
                    throw Err(d, $"undefined variable: {d.Name}");
                }

                _vars[i] = new Var { Name = d.Name, Value = value };
            }
            else
            {
                _vars.Add(new Var { Name = d.Name, Value = value });
            }
        }

        return value;
    }

    // A pipeline whose value is nil yields "no value" (the reference unwraps the nil interface), so a later .field on it is
    // silent, while .field on a nil taken directly from the data is an error.
    private object? EvalCommands(object? dot, PipeNode pipe)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        object? value = null;
        bool have = false;
        foreach (CommandNode cmd in pipe.Cmds)
        {
            value = V.Norm(EvalCommand(dot, cmd, have, value)) ?? V.Missing;
            have = true;
        }

        return value;
    }

    private void NotAFunction(List<Node> args, bool hasFinal, Node at)
    {
        if (args.Count > 1 || hasFinal)
        {
            throw Err(at, "can't give argument to non-function");
        }
    }

    private object? EvalCommand(object? dot, CommandNode cmd, bool hasFinal, object? final)
    {
        Node first = cmd.Args[0];
        switch (first)
        {
            case FieldNode f:
                return EvalFieldChain(dot, dot, f, f.Idents, cmd.Args, hasFinal, final);
            case ChainNode c:
            {
                if (c.Inner is NilNode)
                {
                    throw Err(c, "indirection through explicit nil");
                }

                object? recv = EvalArg(dot, ArgKind.Any, c.Inner);

                // Observed from Go 1.26: a parenthesised pipeline that yields nil (a missing map key through index, a JSON null) hands the
                // field access "no value" rather than a nil pointer, so (index .m "z").x prints <no value>; a bare .nul.x still fails.
                recv ??= V.Missing;
                return EvalFieldChain(dot, recv, c, c.Fields, cmd.Args, hasFinal, final);
            }

            case IdentifierNode id:
                return EvalCall(dot, id.Function, id, cmd.Args, hasFinal, final);
            case PipeNode p:
                NotAFunction(cmd.Args, hasFinal, first);
                return EvalPipeline(dot, p);
            case VariableNode v:
            {
                int i = FindVar(v.Name);
                if (i < 0)
                {
                    throw Err(v, $"undefined variable: {v.Name}");
                }

                object? value = _vars[i].Value;
                if (v.Fields.Length == 0)
                {
                    NotAFunction(cmd.Args, hasFinal, first);
                    return value;
                }

                return EvalFieldChain(dot, value, v, v.Fields, cmd.Args, hasFinal, final);
            }

            case NilNode:
                throw Err(first, "nil is not a command");
            case DotNode:
                NotAFunction(cmd.Args, hasFinal, first);
                return dot;
            default:
                NotAFunction(cmd.Args, hasFinal, first);
                return Constant(first);
        }
    }

    private object NumberValue(NumberNode num)
    {
        if (num.IsUint)
        {
            throw Err(num, $"{num.Text} overflows int");
        }

        return num.AsFloat ? (object)num.Float : num.Int;
    }

    private object? Constant(Node n) => n switch
    {
        BoolNode b => b.Value,
        StringNode s => s.Value,
        NumberNode num => NumberValue(num),
        _ => throw Err(n, "unexpected constant"),
    };

    // ---- fields ----

    private object? EvalFieldChain(object? dot, object? receiver, Node node, string[] idents, IReadOnlyList<Node> args, bool hasFinal, object? final)
    {
        int n = idents.Length;
        for (int i = 0; i < n - 1; i++)
        {
            receiver = EvalField(idents[i], node, hasArgs: false, receiver);
        }

        bool hasArgs = args.Count > 1 || hasFinal;
        return EvalField(idents[n - 1], node, hasArgs, receiver);
    }

    private object? EvalField(string name, Node node, bool hasArgs, object? receiver)
    {
        receiver = V.Norm(receiver);
        if (ReferenceEquals(receiver, V.Missing))
        {
            if (_opts.MissingKey == MissingKeyMode.Error)
            {
                throw Err(node, $"nil data; no entry for key \"{name}\"");
            }

            return V.Missing;
        }

        if (receiver is null)
        {
            throw Err(node, $"nil pointer evaluating interface {{}}.{name}");
        }

        if (V.IsMap(receiver))
        {
            if (hasArgs)
            {
                throw Err(node, $"{name} is not a method but has arguments");
            }

            if (V.MapTryGet(receiver, name, out object? value))
            {
                return value;
            }

            if (_opts.MissingKey == MissingKeyMode.Error)
            {
                throw Err(node, $"map has no entry for key \"{name}\"");
            }

            return V.Missing;
        }

        throw Err(node, $"can't evaluate field {name} in type {V.TypeName(receiver)}");
    }

    // ---- calls ----

    private object? EvalCall(object? dot, TemplateFunction fn, Node node, IReadOnlyList<Node> argNodes, bool hasFinal, object? final)
    {
        string name = fn.Name;
        int given = argNodes.Count - 1 + (hasFinal ? 1 : 0);

        if (fn.Role is FunctionRole.And or FunctionRole.Or)
        {
            if (given < 1)
            {
                throw Err(node, $"wrong number of args for {name}: want at least 1 got 0");
            }

            bool isAnd = fn.Role == FunctionRole.And;
            object? last = null;
            for (int i = 1; i < argNodes.Count; i++)
            {
                last = EvalArg(dot, ArgKind.Any, argNodes[i]);
                if (V.IsTrue(last) != isAnd)
                {
                    return last;
                }
            }

            if (hasFinal)
            {
                last = Coerce(ArgKind.Any, final, node);
            }

            return last;
        }

        int fixedCount = fn.Parameters.Count;
        if (fn.Variadic is null ? given != fixedCount : given < fixedCount)
        {
            string want = fn.Variadic is null ? fixedCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "at least " + fixedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw Err(node, $"wrong number of args for {name}: want {want} got {given.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        var values = new object?[given];
        for (int i = 0; i < given; i++)
        {
            ArgKind kind = i < fixedCount ? fn.Parameters[i] : fn.Variadic!.Value;
            int nodeIndex = i + 1;
            if (i > 0 && ReferenceEquals(fn, Builtins.SliceFunction) && nodeIndex < argNodes.Count && IsInterfaceValued(argNodes[nodeIndex]))
            {
                // Observed from the reference implementation: slice does not unwrap an index that comes straight from a
                // field of the data (it is still an interface value there), so it fails; index does unwrap.
                throw Err(argNodes[nodeIndex], "error calling slice: cannot index slice/array with type interface {}");
            }

            values[i] = nodeIndex < argNodes.Count ? EvalArg(dot, kind, argNodes[nodeIndex]) : Coerce(kind, final, node);
        }

        return Invoke(fn, values, node);
    }

    // An argument expression whose value is still an interface value in the reference: a field of the data, a chain, a
    // range variable over a list or map, or the dot inside such a range.
    private bool IsInterfaceValued(Node n)
    {
        switch (n)
        {
            case FieldNode or ChainNode:
                return true;
            case VariableNode { Fields.Length: > 0 }:
                return true;
            case VariableNode v:
            {
                int i = FindVar(v.Name);
                return i >= 0 && _vars[i].Iface;
            }

            case DotNode:
                return _dotIface;
            default:
                return false;
        }
    }

    private object? Invoke(TemplateFunction fn, object?[] args, Node node)
    {
        _callNode = node;
        try
        {
            object? result = fn.Context is not null ? fn.Context(this, args) : fn.Invoke(args);
            return V.Norm(result);
        }
        catch (TemplateException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InsufficientExecutionStackException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Err(node, $"error calling {fn.Name}: {ex.Message}");
        }
    }

    /// <summary>Calls a function value from the <c>call</c> builtin: same argument checks as a direct call.</summary>
    internal object? CallValue(TemplateFunction fn, object?[] args)
    {
        int fixedCount = fn.Parameters.Count;
        if (fn.Variadic is null ? args.Length != fixedCount : args.Length < fixedCount)
        {
            throw new TemplateFunctionException($"wrong number of args for {fn.Name}: want {fixedCount} got {args.Length}");
        }

        var coerced = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            ArgKind kind = i < fixedCount ? fn.Parameters[i] : fn.Variadic!.Value;
            object? v = V.Norm(args[i]);
            if (ReferenceEquals(v, V.Missing))
            {
                v = null;
            }

            bool ok = kind switch
            {
                ArgKind.Text => v is string,
                ArgKind.Whole => v is long,
                ArgKind.Real => v is double,
                ArgKind.Bool => v is bool,
                _ => true,
            };
            if (!ok)
            {
                throw new TemplateFunctionException($"wrong type for value; expected {KindName(kind)}; got {V.TypeName(v)}");
            }

            coerced[i] = v;
        }

        return fn.Context is not null ? fn.Context(this, coerced) : fn.Invoke(coerced);
    }

    private object? Coerce(ArgKind kind, object? v, Node at)
    {
        v = V.Norm(v);
        bool missing = ReferenceEquals(v, V.Missing);
        if (kind == ArgKind.Any)
        {
            return missing ? null : v;
        }

        bool ok = kind switch
        {
            ArgKind.Text => v is string,
            ArgKind.Whole => v is long,
            ArgKind.Real => v is double,
            ArgKind.Bool => v is bool,
            _ => true,
        };
        if (ok)
        {
            return v;
        }

        string expected = KindName(kind);
        if (missing)
        {
            throw Err(at, $"invalid value; expected {expected}");
        }

        throw Err(at, $"wrong type for value; expected {expected}; got {(v is null ? "interface {}" : V.TypeName(v))}");
    }

    private static string KindName(ArgKind k) => k switch
    {
        ArgKind.Text => "string",
        ArgKind.Whole => "int",
        ArgKind.Real => "float64",
        ArgKind.Bool => "bool",
        _ => "interface {}",
    };

    private object? EvalArg(object? dot, ArgKind kind, Node n)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (n)
        {
            case DotNode:
                return Coerce(kind, dot, n);
            case NilNode:
                if (kind == ArgKind.Any)
                {
                    return null;
                }

                throw Err(n, $"cannot assign nil to {KindName(kind)}");
            case FieldNode f:
                return Coerce(kind, EvalFieldChain(dot, dot, f, f.Idents, new[] { n }, false, null), n);
            case VariableNode v:
            {
                int i = FindVar(v.Name);
                if (i < 0)
                {
                    throw Err(v, $"undefined variable: {v.Name}");
                }

                object? value = _vars[i].Value;
                if (v.Fields.Length > 0)
                {
                    value = EvalFieldChain(dot, value, v, v.Fields, new[] { n }, false, null);
                }

                return Coerce(kind, value, n);
            }

            case PipeNode p:
                return Coerce(kind, EvalPipeline(dot, p), n);
            case IdentifierNode id:
                return Coerce(kind, EvalCall(dot, id.Function, id, new[] { n }, false, null), n);
            case ChainNode c:
            {
                if (c.Inner is NilNode)
                {
                    throw Err(c, "indirection through explicit nil");
                }

                object? recv = EvalArg(dot, ArgKind.Any, c.Inner);

                // Observed from Go 1.26: a parenthesised pipeline that yields nil (a missing map key through index, a JSON null) hands the
                // field access "no value" rather than a nil pointer, so (index .m "z").x prints <no value>; a bare .nul.x still fails.
                recv ??= V.Missing;
                return Coerce(kind, EvalFieldChain(dot, recv, c, c.Fields, new[] { n }, false, null), n);
            }

            case BoolNode b:
                if (kind is ArgKind.Bool or ArgKind.Any)
                {
                    return b.Value;
                }

                throw Err(n, $"expected {KindName(kind)}; found {b.Value.ToString().ToLowerInvariant()}");
            case StringNode s:
                if (kind is ArgKind.Text or ArgKind.Any)
                {
                    return s.Value;
                }

                throw Err(n, $"expected {KindName(kind)}; found \"{s.Value}\"");
            case NumberNode num:
                switch (kind)
                {
                    case ArgKind.Any:
                        return NumberValue(num);
                    case ArgKind.Whole:
                        if (num.IsUint)
                        {
                            throw Err(n, $"{num.Text} overflows int");
                        }

                        if (num.IsInt)
                        {
                            return num.Int;
                        }

                        throw Err(n, $"expected integer; found {num.Text}");
                    case ArgKind.Real:
                        return num.Float;
                    default:
                        throw Err(n, $"expected {KindName(kind)}; found {num.Text}");
                }

            default:
                throw Err(n, "can't handle node as argument");
        }
    }

    // ---- embed ----

    /// <summary>Resolves and renders an embedded template, returning its output.</summary>
    internal string Embed(string reference)
    {
        Node pos = _callNode ?? _tmpl.Root;
        if (reference.Length == 0)
        {
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, "embed: reference is required");
        }

        IReadOnlyList<IEmbedSource>? sources = _opts.EmbedSources;
        if (sources is null || sources.Count == 0)
        {
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(reference)}: no embed source is configured");
        }

        string? referrer = _embedStack.Count > 0 ? _embedStack[^1] : _opts.RootKey;
        EmbedContent? found = null;
        foreach (IEmbedSource source in sources)
        {
            try
            {
                found = source.Resolve(reference, referrer);
            }
            catch (TemplateFunctionException ex)
            {
                throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(reference)}: {ex.Message}", ex);
            }

            if (found is not null)
            {
                break;
            }
        }

        if (found is null)
        {
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(reference)}: not found");
        }

        EmbedContent content = found.Value;
        if (content.Key == _opts.RootKey || _embedStack.Contains(content.Key))
        {
            var chain = new List<string>();
            if (_opts.RootKey is not null)
            {
                chain.Add(_opts.RootKey);
            }

            chain.AddRange(_embedStack);
            chain.Add(content.Key);
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(content.Key)}: cycle detected: {string.Join(" -> ", chain)}");
        }

        if (_embedStack.Count >= _opts.MaxEmbedDepth)
        {
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(content.Key)}: embeds are nested more than {_opts.MaxEmbedDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)} deep");
        }

        if (!content.Text.Contains("{{", StringComparison.Ordinal))
        {
            return content.Text;
        }

        _embedStack.Add(content.Key);
        try
        {
            ParsedTemplate parsed = Template.ParseCached(content.Text, content.Name ?? content.Key, _tmpl.Functions, escape: true);
            var sb = new StringBuilder();
            var nested = new Exec(parsed, _root, sb, _opts, _embedStack);
            nested.Run();
            return sb.ToString();
        }
        catch (TemplateException inner)
        {
            throw _tmpl.Error(TemplateErrorKind.Embed, pos.Pos, $"embed {GoQuoteWriter.Quote(content.Key)}: failed to render nested template: {inner.Message}", inner);
        }
        finally
        {
            _embedStack.RemoveAt(_embedStack.Count - 1);
        }
    }
}
